// DataAccess/DuckDbFactory.cs
using DuckDB.NET.Data;
using System.Data;
using System.IO;

namespace GeoVis.DataAccess
{
    public static class DuckDbFactory
    {
        // 我们在项目运行目录下创建一个本地数据库文件
        private static readonly string DbPath = Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "GeoDataStore.duckdb");
        private static readonly string ConnectionString = $"DataSource={DbPath}";

        /// <summary>
        /// 获取一个打开的 DuckDB 数据库连接
        /// </summary>
        public static IDbConnection GetConnection()
        {
            var connection = new DuckDBConnection(ConnectionString);
            connection.Open();
            return connection;
        }

        /// <summary>
        /// 提供给主界面的状态，用于显示数据库大小
        /// </summary>
        public static string GetDatabaseInfo()
        {
            if (File.Exists(DbPath))
            {
                long length = new FileInfo(DbPath).Length;
                return $"数据库大小: {length / 1024 / 1024} MB";
            }
            return "数据库未初始化";
        }
    }
}