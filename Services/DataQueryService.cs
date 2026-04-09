// Services/DataQueryService.cs
using Dapper;
using GeoVis.DataAccess;
using GeoVis.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Linq;

namespace GeoVis.Services
{
    public class DataQueryService
    {
        /// <summary>
        /// 将外部大 CSV 文件极速导入到 DuckDB 的本地格式中
        /// 相当于替代了 Python 中的 can4merge.py
        /// </summary>
        public async Task<(int rowCount, long elapsedMs)> ImportOdCsvAsync(string csvPath)
        {
            var sw = Stopwatch.StartNew();

            // 使用 Task.Run 确保耗时操作不会卡死 UI 线程
            int count = await Task.Run(() =>
            {
                using var conn = DuckDbFactory.GetConnection();

                // 1. 如果表已存在则删除
                conn.Execute("DROP TABLE IF EXISTS od_data;");

                // 2. DuckDB 魔法：直接从 CSV 创建表并推断类型，速度极快！
                string sql = $@"
                    CREATE TABLE od_data AS 
                    SELECT * FROM read_csv_auto('{csvPath.Replace("\\", "/")}');
                ";
                conn.Execute(sql);

                // 3. 返回导入的总行数
                return conn.QuerySingle<int>("SELECT COUNT(*) FROM od_data;");
            });

            sw.Stop();
            return (count, sw.ElapsedMilliseconds);
        }

        /// <summary>
        /// 统计逐时的总流量和连接数 (完美复刻 od_weight_edge.py 的数据处理核心逻辑)
        /// </summary>
        public async Task<IEnumerable<HourlyNetworkMetric>> GetHourlyNetworkMetricsAsync()
        {
            return await Task.Run(() =>
            {
                using var conn = DuckDbFactory.GetConnection();

                // 仅需这一段 SQL，DuckDB 能在毫秒级完成 Pandas 需要算很久的 groupby 和 nunique
                string sql = @"
                    SELECT 
                        start_date AS StartDate,
                        start_hour AS StartHour,
                        SUM(trip_cnt) AS TotalWeight,
                        COUNT(DISTINCT CONCAT(LEAST(o_grid, d_grid), '_', GREATEST(o_grid, d_grid))) AS EdgeNumber
                    FROM od_data
                    GROUP BY start_date, start_hour
                    ORDER BY start_date, start_hour;
                ";

                // Dapper 自动将查询结果映射到 C# 的 HourlyNetworkMetric 实体列表
                return conn.Query<HourlyNetworkMetric>(sql);
            });
        }

        /// <summary>
        /// 聚合每个网格的发出量 (SELECT o_grid, SUM(trip_cnt) ...)
        /// </summary>
        public async Task<Dictionary<string, long>> GetGridOutflowAsync()
        {
            return await Task.Run(() =>
            {
                using var conn = DuckDbFactory.GetConnection();
                // 将 o_grid 转为字符串方便与 GeoJSON 里的 cid 匹配
                string sql = @"
                    SELECT 
                        CAST(o_grid AS VARCHAR) AS Id, 
                        CAST(SUM(trip_cnt) AS BIGINT) AS Value 
                    FROM od_data 
                    GROUP BY o_grid;
                ";
                // 用 Dapper 查出列表后，瞬间转为 Dictionary 字典，方便后续 O(1) 极速匹配
                var list = conn.Query(sql);
                var dict = new Dictionary<string, long>();
                foreach (var row in list)
                {
                    dict[row.Id] = row.Value;
                }
                return dict;
            });
        }

        /// <summary>
        /// 获取 OD 数据中所有的唯一日期，供 UI 下拉框使用
        /// </summary>
        public async Task<List<int>> GetAvailableDatesAsync()
        {
            return await Task.Run(() =>
            {
                using var conn = DuckDbFactory.GetConnection();
                return conn.Query<int>("SELECT DISTINCT start_date FROM od_data ORDER BY start_date;").ToList();
            });
        }

        /// <summary>
        /// 终极空间查询：统一支持 OD 与 驻留 的【绝对值分布】与【时空多基准做差】模型
        /// </summary>
        public async Task<Dictionary<string, (long Val1, long Val2)>> GetSpatialDataAsync(
            string mode, string analysisMode, string odMetric, int targetDate, int targetHour,
            string diffMode = "与上一小时做差", List<int> refDates = null)
        {
            return await Task.Run(() =>
            {
                using var conn = DuckDbFactory.GetConnection();
                string sql = "";
                string dateStr = DateTime.ParseExact(targetDate.ToString(), "yyyyMMdd", null).ToString("yyyy-MM-dd");
                bool isDiff = analysisMode == "时空环比做差";

                if (mode == "OD 轨迹流向")
                {
                    if (!isDiff)
                    {
                        // 绝对值老逻辑：Val1=出, Val2=入
                        sql = $@"
                            SELECT CAST(grid_id AS VARCHAR) AS Id, CAST(SUM(outflow) AS BIGINT) AS Val1, CAST(SUM(inflow) AS BIGINT) AS Val2
                            FROM (
                                SELECT o_grid AS grid_id, trip_cnt as outflow, 0 as inflow FROM od_data WHERE start_date = {targetDate} AND start_hour = {targetHour}
                                UNION ALL
                                SELECT d_grid AS grid_id, 0 as outflow, trip_cnt as inflow FROM od_data WHERE start_date = {targetDate} AND end_hour = {targetHour}
                            ) GROUP BY grid_id;
                        ";
                    }
                    else
                    {
                        // 强大的 OD 环比做差：根据前端选定的指标 (总/出/入) 动态选取计算列
                        string colSelect = odMetric == "流出流量" ? "outflow" : odMetric == "流入流量" ? "inflow" : "total_flow";
                        string refDateList = refDates != null && refDates.Any() ? string.Join(",", refDates) : targetDate.ToString();

                        // 核心逻辑：当前小时选定指标为 Val1，当前 - 历史(上一小时或多日同小时均值) 为 Val2
                        sql = $@"
                            WITH base_od AS (
                                SELECT start_date, start_hour as hour, o_grid AS grid_id, trip_cnt as outflow, 0 as inflow, trip_cnt as total_flow FROM od_data WHERE start_date IN ({targetDate},{refDateList}) AND start_hour IN ({targetHour},{targetHour - 1})
                                UNION ALL
                                SELECT start_date, end_hour as hour, d_grid AS grid_id, 0 as outflow, trip_cnt as inflow, trip_cnt as total_flow FROM od_data WHERE start_date IN ({targetDate},{refDateList}) AND end_hour IN ({targetHour},{targetHour - 1})
                            ),
                            current_hr AS ( SELECT grid_id, SUM({colSelect}) as val FROM base_od WHERE start_date = {targetDate} AND hour = {targetHour} GROUP BY grid_id ),
                            prev_hr AS ( ";

                        if (diffMode == "与选定日期同时间做差" && refDates != null && refDates.Any())
                            sql += $"SELECT grid_id, AVG(daily_val) as val FROM (SELECT start_date, grid_id, SUM({colSelect}) as daily_val FROM base_od WHERE start_date IN ({refDateList}) AND hour = {targetHour} GROUP BY start_date, grid_id) GROUP BY grid_id";
                        else
                            sql += $"SELECT grid_id, SUM({colSelect}) as val FROM base_od WHERE start_date = {targetDate} AND hour = {targetHour - 1} GROUP BY grid_id";

                        sql += ") SELECT CAST(c.grid_id AS VARCHAR) AS Id, CAST(c.val AS BIGINT) AS Val1, CAST(COALESCE(c.val,0) - COALESCE(p.val,0) AS BIGINT) AS Val2 FROM current_hr c LEFT JOIN prev_hr p ON c.grid_id = p.grid_id;";
                    }
                }
                else if (mode == "网格驻留人口")
                {
                    if (!isDiff)
                    {
                        // 驻留绝对值：只有 Val1
                        sql = $"SELECT CAST(grid_id AS VARCHAR) AS Id, CAST(SUM(signal_count) AS BIGINT) AS Val1, 0 AS Val2 FROM mobility_data WHERE CAST(date AS VARCHAR) LIKE '%{dateStr}%' AND hour = {targetHour} GROUP BY grid_id;";
                    }
                    else
                    {
                        // 驻留做差
                        string refDateList = refDates != null && refDates.Any() ? string.Join(",", refDates.Select(d => $"'{DateTime.ParseExact(d.ToString(), "yyyyMMdd", null):yyyy-MM-dd}'")) : $"'{dateStr}'";

                        sql = $@"WITH current_hr AS (SELECT grid_id, SUM(signal_count) as val FROM mobility_data WHERE CAST(date AS VARCHAR) LIKE '%{dateStr}%' AND hour = {targetHour} GROUP BY grid_id),
                                      prev_hr AS ( ";
                        if (diffMode == "与选定日期同时间做差" && refDates != null && refDates.Any())
                            sql += $"SELECT grid_id, AVG(daily_val) as val FROM (SELECT date, grid_id, SUM(signal_count) as daily_val FROM mobility_data WHERE CAST(date AS VARCHAR) IN ({refDateList}) AND hour = {targetHour} GROUP BY date, grid_id) GROUP BY grid_id";
                        else
                            sql += $"SELECT grid_id, SUM(signal_count) as val FROM mobility_data WHERE CAST(date AS VARCHAR) LIKE '%{dateStr}%' AND hour = {targetHour - 1} GROUP BY grid_id";

                        sql += ") SELECT CAST(c.grid_id AS VARCHAR) AS Id, CAST(c.val AS BIGINT) AS Val1, CAST(COALESCE(c.val,0) - COALESCE(p.val,0) AS BIGINT) AS Val2 FROM current_hr c LEFT JOIN prev_hr p ON c.grid_id = p.grid_id;";
                    }
                }
                else if (mode == "月度常住人口")
                {
                    sql = "SELECT CAST(grid_id AS VARCHAR) AS Id, CAST(SUM(real_pop_sum) AS BIGINT) AS Val1, CAST(SUM(unicom_cnt) AS BIGINT) AS Val2 FROM pop_data GROUP BY grid_id;";
                }

                var list = conn.Query(sql);
                var dict = new Dictionary<string, (long, long)>();
                foreach (var row in list)
                {
                    string id = Convert.ToString(row.Id);
                    long v1 = Convert.ToInt64(row.Val1);
                    long v2 = Convert.ToInt64(row.Val2);
                    dict[id] = (v1, v2);
                }
                return dict;
            });
        }
        /// <summary>
        /// 【新增】：极速获取选定气象站集合在【指定日期和小时】的降水量
        /// </summary>
        public async Task<Dictionary<string, double>> GetMapRainfallAsync(List<string> stationNames, int targetDate, int targetHour)
        {
            if (stationNames == null || !stationNames.Any()) return new Dictionary<string, double>();
            return await Task.Run(() =>
            {
                using var conn = DuckDbFactory.GetConnection();
                string dateStr = DateTime.ParseExact(targetDate.ToString(), "yyyyMMdd", null).ToString("yyyy-MM-dd");

                // 为了兼容可能存在的毫秒格式，采用 LIKE '%YYYY-MM-DD HH:%' 模糊匹配
                string timePattern = $"%{dateStr} {targetHour:D2}:%";

                // 【核心修复】：UI传过来的是中文站名，所以必须用 station 字段过滤，而不是 station_id！
                string stationsIn = string.Join(",", stationNames.Select(s => $"'{s}'"));

                // 查询时把 station_id 选出来作为 X 轴图例返回（简短且更具学术性）
                string sql = $"SELECT station_id AS Station, precip_mm AS Value FROM rainfall_data WHERE CAST(timestamp AS VARCHAR) LIKE '{timePattern}' AND station IN ({stationsIn});";

                var list = conn.Query(sql);
                var dict = new Dictionary<string, double>();
                foreach (var row in list)
                {
                    try { dict[row.Station.ToString()] = Convert.ToDouble(row.Value); } catch { }
                }
                return dict;
            });
        }

        /// <summary>
        /// 【核心升级】：通用 CSV 导入方法。支持千万级数据瞬间建表。
        /// </summary>
        public async Task<(int rowCount, long elapsedMs)> ImportTableAsync(string tableName, string csvPath)
        {
            var sw = Stopwatch.StartNew();
            int count = await Task.Run(() =>
            {
                using var conn = DuckDbFactory.GetConnection();
                conn.Execute($"DROP TABLE IF EXISTS {tableName};");
                // read_csv_auto 会自动推断 timestamp、数值等类型
                conn.Execute($"CREATE TABLE {tableName} AS SELECT * FROM read_csv_auto('{csvPath.Replace("\\", "/")}');");
                return conn.QuerySingle<int>($"SELECT COUNT(*) FROM {tableName};");
            });
            sw.Stop();
            return (count, sw.ElapsedMilliseconds);
        }

        /// <summary>
        /// 查询数据库中存在的所有气象站点名称
        /// </summary>
        public async Task<List<string>> GetRainfallStationsAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var conn = DuckDbFactory.GetConnection();
                    var list = conn.Query<string>("SELECT DISTINCT station FROM rainfall_data ORDER BY station;").ToList();
                    list.Insert(0, "NONE (关闭降水图层)");
                    list.Insert(1, "ALL STATIONS (Overlay 全叠加)"); // 新增全叠加选项
                    list.Insert(2, "ALL STATIONS (Average 平均)");
                    return list;
                }
                catch { return new List<string>(); }
            });
        }

        /// <summary>
        /// 获取指定站点的逐小时降水数据
        /// </summary>
        public async Task<Dictionary<DateTime, double>> GetRainfallDataAsync(string stationName)
        {
            return await Task.Run(() =>
            {
                using var conn = DuckDbFactory.GetConnection();
                // 注意：DuckDB 读取的 timestamp 会自动转为 C# 的 DateTime
                string sql = $"SELECT timestamp AS Time, precip_mm AS Value FROM rainfall_data WHERE station = '{stationName}';";
                var list = conn.Query(sql);

                var dict = new Dictionary<DateTime, double>();
                foreach (var row in list)
                {
                    try
                    {
                        // 使用 Convert 防御类型推断错误
                        DateTime dt = Convert.ToDateTime(row.Time);
                        double val = Convert.ToDouble(row.Value);
                        dict[dt] = val;
                    }
                    catch { continue; } // 如果有脏数据跳过
                }
                return dict;
            });
        }

        // 驻留人口数据实体
        public class HourlyMobilityMetric
        {
            public string DateStr { get; set; }
            public int Hour { get; set; }
            public long TotalSignal { get; set; }
        }

        /// <summary>
        /// 获取逐时驻留人口总量
        /// </summary>
        public async Task<List<HourlyMobilityMetric>> GetHourlyMobilityMetricsAsync()
        {
            return await Task.Run(() =>
            {
                using var conn = DuckDbFactory.GetConnection();
                try
                {
                    // date 在 csv 里通常是 2023-05-01 这种格式
                    string sql = "SELECT CAST(date AS VARCHAR) AS DateStr, hour AS Hour, CAST(SUM(signal_count) AS BIGINT) AS TotalSignal FROM mobility_data GROUP BY date, hour ORDER BY date, hour;";
                    return conn.Query<HourlyMobilityMetric>(sql).ToList();
                }
                catch { return new List<HourlyMobilityMetric>(); }
            });
        }

        /// <summary>
        /// 获取降水数据，返回结构为：字典 <站点名, 字典<时间, 降水量>>
        /// </summary>
        public async Task<Dictionary<string, Dictionary<DateTime, double>>> GetRainfallMultiDataAsync(string stationName)
        {
            return await Task.Run(() =>
            {
                using var conn = DuckDbFactory.GetConnection();
                string sql;
                if (stationName == "ALL STATIONS (Overlay 全叠加)")
                {
                    // 【修改】：使用 station_id 代替 station 作为图例标识
                    sql = "SELECT station_id AS Station, timestamp AS Time, precip_mm AS Value FROM rainfall_data;";
                }
                else if (stationName == "ALL STATIONS (Average 平均)")
                {
                    sql = "SELECT 'Average' AS Station, timestamp AS Time, AVG(precip_mm) AS Value FROM rainfall_data GROUP BY timestamp;";
                }
                else
                {
                    // 【修改】：条件查的是 station，但返回的是 station_id 给图表画图例用！
                    sql = $"SELECT station_id AS Station, timestamp AS Time, precip_mm AS Value FROM rainfall_data WHERE station = '{stationName}';";
                }

                var list = conn.Query(sql);
                var result = new Dictionary<string, Dictionary<DateTime, double>>();

                foreach (var row in list)
                {
                    try
                    {
                        string st = row.Station.ToString();
                        DateTime dt = Convert.ToDateTime(row.Time);
                        double val = Convert.ToDouble(row.Value);

                        if (!result.ContainsKey(st)) result[st] = new Dictionary<DateTime, double>();
                        result[st][dt] = val;
                    }
                    catch { continue; }
                }
                return result;
            });
        }

        /// <summary>
        /// 极速清空指定的 DuckDB 数据表
        /// </summary>
        public async Task ClearTableAsync(string tableName)
        {
            await Task.Run(() =>
            {
                using var conn = DuckDbFactory.GetConnection();
                conn.Execute($"DROP TABLE IF EXISTS {tableName};");
            });
        }

        // --- 【第 1 步新增：OD 飞线数据实体】 ---
        public class OdFlowLine
        {
            public string OGrid { get; set; }
            public string DGrid { get; set; }
            public long DiffVal { get; set; } // 绝对值模式下为流量，做差模式下为差值
        }

        /// <summary>
        /// 获取 Top N 的 OD 流动飞线数据
        /// </summary>
        public async Task<List<OdFlowLine>> GetTopOdFlowLinesAsync(
            string analysisMode, int targetDate, int targetHour,
            string diffMode = "与上一小时做差", List<int> refDates = null,
            string filterGridId = null, int topN = 10,
            bool excludeIntraZonal = false,
            string odDisplayMode = "总流量",
            string trendMode = "变化绝对值最大") // 【新增参数】
        {
            return await Task.Run(() =>
            {
                using var conn = DuckDbFactory.GetConnection();
                string sql = "";
                bool isDiff = analysisMode == "时空环比做差";

                string oCol = isDiff ? "OGrid" : "o_grid";
                string dCol = isDiff ? "DGrid" : "d_grid";
                string filterClause = "";

                // 1. 全局与局部逻辑：只有点了网格，才应用“流出/流入”指标过滤
                if (!string.IsNullOrEmpty(filterGridId))
                {
                    if (odDisplayMode == "流出流量")
                        filterClause = $" AND {oCol} = '{filterGridId}' ";
                    else if (odDisplayMode == "流入流量")
                        filterClause = $" AND {dCol} = '{filterGridId}' ";
                    else
                        filterClause = $" AND ({oCol} = '{filterGridId}' OR {dCol} = '{filterGridId}') ";
                }

                // 2. 剔除自流
                if (excludeIntraZonal) filterClause += $" AND {oCol} != {dCol} ";

                // 3. 构建 SQL
                if (!isDiff)
                {
                    // 绝对值模式：不支持增减趋势，永远按流量降序
                    sql = $@"
                        SELECT CAST(o_grid AS VARCHAR) AS OGrid, CAST(d_grid AS VARCHAR) AS DGrid, CAST(SUM(trip_cnt) AS BIGINT) AS DiffVal
                        FROM od_data WHERE start_date = {targetDate} AND start_hour = {targetHour}
                        {filterClause}
                        GROUP BY o_grid, d_grid ORDER BY DiffVal DESC LIMIT {topN};
                    ";
                }
                else
                {
                    // 【核心升级】：做差模式下的增/减过滤与排序逻辑
                    string orderClause = "ORDER BY ABS(DiffVal) DESC";
                    if (trendMode == "仅看激增 (红线)")
                    {
                        filterClause += " AND DiffVal > 0 ";
                        orderClause = "ORDER BY DiffVal DESC"; // 取正数最大
                    }
                    else if (trendMode == "仅看锐减 (蓝线)")
                    {
                        filterClause += " AND DiffVal < 0 ";
                        orderClause = "ORDER BY DiffVal ASC";  // 取负数最深（如 -500 排在 -100 前面）
                    }

                    string refDateList = refDates != null && refDates.Any() ? string.Join(",", refDates) : targetDate.ToString();
                    sql = $@"
                        WITH base_od AS (
                            SELECT start_date, start_hour as hour, o_grid, d_grid, trip_cnt as flow FROM od_data 
                            WHERE (start_date = {targetDate} AND start_hour IN ({targetHour}, {targetHour - 1}))
                               OR (start_date IN ({refDateList}) AND start_hour = {targetHour})
                        ),
                        current_hr AS ( SELECT o_grid, d_grid, SUM(flow) as val FROM base_od WHERE start_date = {targetDate} AND hour = {targetHour} GROUP BY o_grid, d_grid ),
                        prev_hr AS ( ";

                    if (diffMode == "与选定日期同时间做差" && refDates != null && refDates.Any())
                        sql += $"SELECT o_grid, d_grid, AVG(daily_val) as val FROM (SELECT start_date, o_grid, d_grid, SUM(flow) as daily_val FROM base_od WHERE start_date IN ({refDateList}) AND hour = {targetHour} GROUP BY start_date, o_grid, d_grid) GROUP BY o_grid, d_grid";
                    else
                        sql += $"SELECT o_grid, d_grid, SUM(flow) as val FROM base_od WHERE start_date = {targetDate} AND hour = {targetHour - 1} GROUP BY o_grid, d_grid";

                    sql += $@"
                        ),
                        joined_diff AS (
                            SELECT COALESCE(c.o_grid, p.o_grid) AS OGrid, COALESCE(c.d_grid, p.d_grid) AS DGrid, (COALESCE(c.val, 0) - COALESCE(p.val, 0)) AS DiffVal
                            FROM current_hr c FULL OUTER JOIN prev_hr p ON c.o_grid = p.o_grid AND c.d_grid = p.d_grid
                        )
                        SELECT CAST(OGrid AS VARCHAR) AS OGrid, CAST(DGrid AS VARCHAR) AS DGrid, CAST(DiffVal AS BIGINT) AS DiffVal
                        FROM joined_diff WHERE 1=1 {filterClause} {orderClause} LIMIT {topN};
                    ";
                }
                return conn.Query<OdFlowLine>(sql).ToList();
            });
        }

        /// <summary>
        /// 查询数据库中存在的所有气温站点名称
        /// </summary>
        public async Task<List<string>> GetTemperatureStationsAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var conn = DuckDbFactory.GetConnection();
                    // 仅从气温表查询
                    var list = conn.Query<string>("SELECT DISTINCT station FROM temperature_data ORDER BY station;").ToList();
                    list.Insert(0, "NONE (关闭气温曲线)");
                    list.Insert(1, "ALL STATIONS (Overlay 全叠加)");
                    list.Insert(2, "ALL STATIONS (Average 平均)");
                    return list;
                }
                catch { return new List<string>(); }
            });
        }

        /// <summary>
        /// 获取气温数据，返回结构为：字典 <站点名, 字典<时间, 气温值>>
        /// </summary>
        public async Task<Dictionary<string, Dictionary<DateTime, double>>> GetTemperatureMultiDataAsync(string stationName)
        {
            return await Task.Run(() =>
            {
                using var conn = DuckDbFactory.GetConnection();
                string sql;

                // 【科学重构】：将包络带计算全部下推至 DuckDB，确保数据口径绝对统一！
                if (stationName == "ALL STATIONS (Overlay 全叠加)")
                {
                    sql = @"
                        SELECT 'Min' AS Station, timestamp AS Time, CAST(MIN(temperature_c) AS DOUBLE) AS Value FROM temperature_data GROUP BY timestamp 
                        UNION ALL 
                        SELECT 'Max' AS Station, timestamp AS Time, CAST(MAX(temperature_c) AS DOUBLE) AS Value FROM temperature_data GROUP BY timestamp 
                        UNION ALL 
                        SELECT 'Avg' AS Station, timestamp AS Time, CAST(AVG(temperature_c) AS DOUBLE) AS Value FROM temperature_data GROUP BY timestamp;
                    ";
                }
                else if (stationName == "ALL STATIONS (Average 平均)")
                {
                    sql = "SELECT 'Average' AS Station, timestamp AS Time, CAST(AVG(temperature_c) AS DOUBLE) AS Value FROM temperature_data GROUP BY timestamp;";
                }
                else
                {
                    sql = $"SELECT station_id AS Station, timestamp AS Time, CAST(temperature_c AS DOUBLE) AS Value FROM temperature_data WHERE station = '{stationName}';";
                }

                var list = conn.Query(sql);
                var result = new Dictionary<string, Dictionary<DateTime, double>>();

                foreach (var row in list)
                {
                    try
                    {
                        string st = row.Station.ToString();
                        DateTime dt = Convert.ToDateTime(row.Time);
                        double val = Convert.ToDouble(row.Value);

                        if (!result.ContainsKey(st)) result[st] = new Dictionary<DateTime, double>();
                        result[st][dt] = val;
                    }
                    catch { continue; }
                }
                return result;
            });
        }
    }
}