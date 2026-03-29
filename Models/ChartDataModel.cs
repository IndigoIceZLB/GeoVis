// Models/ChartDataModel.cs
namespace GeoVis.Models
{
    // 用于接收按小时聚合的网络指标 (对应 od_weight_edge.py)
    public class HourlyNetworkMetric
    {
        public int StartDate { get; set; }
        public int StartHour { get; set; }
        public long TotalWeight { get; set; } // 总流量 (trip_cnt_sum)
        public long EdgeNumber { get; set; }  // 边数量 (nunique_edge)
    }
}