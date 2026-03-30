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
        /// 终极空间查询：根据模式、日期、小时，返回统一格式的数值字典 (Id -> Val1, Val2)
        /// </summary>
        public async Task<Dictionary<string, (long Val1, long Val2)>> GetSpatialDataAsync(string mode, int targetDate, int targetHour)
        {
            return await Task.Run(() =>
            {
                using var conn = DuckDbFactory.GetConnection();
                string sql = "";

                // 将 20230501 转为 2023-05-01，以适配不同 CSV 的日期格式
                string dateStr = DateTime.ParseExact(targetDate.ToString(), "yyyyMMdd", null).ToString("yyyy-MM-dd");

                if (mode == "OD 流出与流入")
                {
                    // Val1 = 流出, Val2 = 流入
                    sql = $@"
                        SELECT CAST(grid_id AS VARCHAR) AS Id, CAST(SUM(outflow) AS BIGINT) AS Val1, CAST(SUM(inflow) AS BIGINT) AS Val2
                        FROM (
                            SELECT o_grid AS grid_id, SUM(trip_cnt) as outflow, 0 as inflow FROM od_data WHERE start_date = {targetDate} AND start_hour = {targetHour} GROUP BY o_grid
                            UNION ALL
                            SELECT d_grid AS grid_id, 0 as outflow, SUM(trip_cnt) as inflow FROM od_data WHERE start_date = {targetDate} AND end_hour = {targetHour} GROUP BY d_grid
                        ) GROUP BY grid_id;
                    ";
                }
                else if (mode == "驻留人口与变化 (做差)")
                {
                    // 【核心做差魔法】：Val1 = 当前小时驻留量, Val2 = 环比变化量 (当前小时 - 上一小时)
                    sql = $@"
                        WITH current_hr AS (SELECT grid_id, SUM(signal_count) as val FROM mobility_data WHERE CAST(date AS VARCHAR) LIKE '%{dateStr}%' AND hour = {targetHour} GROUP BY grid_id),
                             prev_hr AS (SELECT grid_id, SUM(signal_count) as val FROM mobility_data WHERE CAST(date AS VARCHAR) LIKE '%{dateStr}%' AND hour = {targetHour - 1} GROUP BY grid_id)
                        SELECT 
                            CAST(c.grid_id AS VARCHAR) AS Id, 
                            CAST(c.val AS BIGINT) AS Val1, 
                            CAST(COALESCE(c.val,0) - COALESCE(p.val,0) AS BIGINT) AS Val2
                        FROM current_hr c
                        LEFT JOIN prev_hr p ON c.grid_id = p.grid_id;
                    ";
                }
                else if (mode == "月度常住人口")
                {
                    // 静态月度数据，不区分日期和小时。Val1 = 真实人口, Val2 = 联通信令
                    sql = "SELECT CAST(grid_id AS VARCHAR) AS Id, CAST(SUM(real_pop_sum) AS BIGINT) AS Val1, CAST(SUM(unicom_cnt) AS BIGINT) AS Val2 FROM pop_data GROUP BY grid_id;";
                }

                var list = conn.Query(sql);
                var dict = new Dictionary<string, (long, long)>();
                foreach (var row in list)
                {
                    // 显式转换，打破 dynamic 的魔咒
                    string id = Convert.ToString(row.Id);
                    long v1 = Convert.ToInt64(row.Val1);
                    long v2 = Convert.ToInt64(row.Val2);

                    dict[id] = (v1, v2);
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
                    sql = "SELECT station AS Station, timestamp AS Time, precip_mm AS Value FROM rainfall_data;";
                }
                else if (stationName == "ALL STATIONS (Average 平均)")
                {
                    sql = "SELECT 'Average' AS Station, timestamp AS Time, AVG(precip_mm) AS Value FROM rainfall_data GROUP BY timestamp;";
                }
                else
                {
                    sql = $"SELECT station AS Station, timestamp AS Time, precip_mm AS Value FROM rainfall_data WHERE station = '{stationName}';";
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
    }
}