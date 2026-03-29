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
    }
}