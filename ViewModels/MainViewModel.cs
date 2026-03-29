// ViewModels/MainViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GeoVis.Services;
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32; // 用于弹窗选择文件

namespace GeoVis.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly DataQueryService _dataService;

        [ObservableProperty]
        private string _applicationTitle = "GeoResearchVis - 空间数据极速分析";

        [ObservableProperty]
        private string _statusMessage = "就绪 - 100% 离线模式";

        [ObservableProperty]
        private string _queryResultText = "图表与地图渲染区 (等待加载...)";

        public MainViewModel()
        {
            _dataService = new DataQueryService();
            StatusMessage = GeoVis.DataAccess.DuckDbFactory.GetDatabaseInfo();
        }

        // [RelayCommand] 会被 Toolkit 自动生成一个可绑定的命令 ImportCsvCommand
        [RelayCommand]
        private async Task ImportCsvAsync()
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
                Title = "选择 OD 数据长表 (od_flatten.csv)"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                StatusMessage = "正在导入并构建列式索引，请稍候...";
                QueryResultText = "数据库引擎全速运转中...\n(这通常只需几秒钟)";

                try
                {
                    // 1. 导入数据
                    var (rowCount, elapsedMs) = await _dataService.ImportOdCsvAsync(openFileDialog.FileName);

                    // 2. 紧接着运行逐时统计
                    var metrics = await _dataService.GetHourlyNetworkMetricsAsync();
                    var metricsList = metrics.ToList();

                    StatusMessage = $"导入成功 | {GeoVis.DataAccess.DuckDbFactory.GetDatabaseInfo()}";

                    // 3. 在右侧大屏幕上打印结果预览
                    QueryResultText = $"✅ 极速导入完成！\n" +
                                      $"解析文件: {openFileDialog.SafeFileName}\n" +
                                      $"总行数: {rowCount:N0} 行\n" +
                                      $"导入耗时: {elapsedMs} 毫秒\n\n" +
                                      $"📊 逐时网络指标计算完成 (共 {metricsList.Count} 个小时节点):\n" +
                                      $"[首条记录] 日期:{metricsList.First().StartDate} {metricsList.First().StartHour}时 | 总流量:{metricsList.First().TotalWeight} | 边数:{metricsList.First().EdgeNumber}\n" +
                                      $"[末条记录] 日期:{metricsList.Last().StartDate} {metricsList.Last().StartHour}时 | 总流量:{metricsList.Last().TotalWeight} | 边数:{metricsList.Last().EdgeNumber}\n\n" +
                                      $"下一步，我们将使用 ScottPlot 将这些数据绘制成科研学术级双 Y 轴曲线图！";
                }
                catch (Exception ex)
                {
                    QueryResultText = $"❌ 发生错误:\n{ex.Message}";
                    StatusMessage = "导入失败";
                }
            }
        }
    }
}