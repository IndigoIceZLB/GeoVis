// Services/DataQueryService.cs
using Dapper;
using GeoVis.DataAccess;
using GeoVis.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

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
        /// 根据指定的日期和小时，极速计算每个网格的【流出量】和【流入量】
        /// </summary>
        public async Task<Dictionary<string, (long Outflow, long Inflow)>> GetGridFlowByTimeAsync(int targetDate, int targetHour)
        {
            return await Task.Run(() =>
            {
                using var conn = DuckDbFactory.GetConnection();

                string sql = $@"
                    SELECT 
                        CAST(grid_id AS VARCHAR) AS Id, 
                        CAST(SUM(outflow) AS BIGINT) AS Outflow, 
                        CAST(SUM(inflow) AS BIGINT) AS Inflow
                    FROM (
                        SELECT o_grid AS grid_id, SUM(trip_cnt) as outflow, 0 as inflow 
                        FROM od_data WHERE start_date = {targetDate} AND start_hour = {targetHour} GROUP BY o_grid
                        UNION ALL
                        SELECT d_grid AS grid_id, 0 as outflow, SUM(trip_cnt) as inflow 
                        FROM od_data WHERE start_date = {targetDate} AND end_hour = {targetHour} GROUP BY d_grid
                    )
                    GROUP BY grid_id;
                ";

                var list = conn.Query(sql);
                var dict = new Dictionary<string, (long, long)>();

                foreach (var row in list)
                {
                    // 【修复报错】：将 dynamic 对象显式转换为强类型的 string 和 long
                    string id = row.Id.ToString();
                    long outV = Convert.ToInt64(row.Outflow);
                    long inV = Convert.ToInt64(row.Inflow);

                    dict[id] = (outV, inV);
                }

                return dict;
            });
        }
    }
}