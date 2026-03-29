// ViewModels/MainViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GeoVis.Models;
using GeoVis.Services;
using Microsoft.Win32; // 用于弹窗选择文件
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json.Nodes; // .NET 的极速 JSON 处理库
using System.IO;
using System.Collections.Generic;


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

        // 新增一个属性：用来存放查询出的网络指标数据
        [ObservableProperty]
        private List<HourlyNetworkMetric> _networkMetricsData;

        [ObservableProperty] private List<int> _availableDates;
        [ObservableProperty] private int _selectedDate;
        [ObservableProperty] private int _selectedHour = 8; // 默认早上 8 点

        // 用于在内存中缓存基础的 GeoJSON 字符串，避免频繁读盘
        private string _baseGeoJson = null;

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

                    NetworkMetricsData = metricsList;
                }
                catch (Exception ex)
                {
                    QueryResultText = $"❌ 发生错误:\n{ex.Message}";
                    StatusMessage = "导入失败";
                }
            }
        }

        // 定义一个事件，用来通知 View 层往 WebView2 发送 JSON
        public event Action<string> OnGeoJsonReadyToSend;

        [RelayCommand]
        private async Task LoadAndRenderMapAsync()
        {
            var openFileDialog = new OpenFileDialog { Filter = "GeoJSON|*.geojson" };
            if (openFileDialog.ShowDialog() == true)
            {
                StatusMessage = "加载底图与时间切片数据中...";
                try
                {
                    // 1. 读取 GeoJSON 缓存到内存
                    _baseGeoJson = await Task.Run(() => File.ReadAllText(openFileDialog.FileName));

                    // 2. 获取可用的日期并默认选中第一个
                    var dates = await _dataService.GetAvailableDatesAsync();
                    if (dates.Any())
                    {
                        AvailableDates = dates;
                        SelectedDate = dates.First();
                    }

                    // 3. 触发一次渲染.注释掉下面这行，避免瞬间触发两次渲染卡死浏览器！
                    //await UpdateMapByTimeAsync();
                }
                catch (Exception ex) { StatusMessage = $"渲染失败: {ex.Message}"; }
            }
        }

        // 当 UI 下拉框选择了新日期时，Toolkit 会自动调用这个方法
        partial void OnSelectedDateChanged(int value)
        {
            _ = UpdateMapByTimeAsync();
        }

        // 当 UI 滑块拖动了新小时时，Toolkit 会自动调用这个方法
        partial void OnSelectedHourChanged(int value)
        {
            _ = UpdateMapByTimeAsync();
        }

        // 核心：根据当前选中的时间和小时，重新计算并推送 JSON
        private async Task UpdateMapByTimeAsync()
        {
            if (string.IsNullOrEmpty(_baseGeoJson) || SelectedDate == 0) return;

            // 1. 瞬间从 DuckDB 获取这个小时的流出流入字典
            var flowDict = await _dataService.GetGridFlowByTimeAsync(SelectedDate, SelectedHour);

            // 2. 注入数据到 JSON (由于在内存中且用原生 JsonNode 处理，耗时通常不到 50ms)
            string modifiedJson = await Task.Run(() =>
            {
                var jNode = JsonNode.Parse(_baseGeoJson);
                foreach (var feature in jNode["features"].AsArray())
                {
                    var props = feature["properties"];
                    string cid = props["cid"]?.ToString();

                    long outV = 0, inV = 0;
                    if (flowDict.TryGetValue(cid, out var vals))
                    {
                        outV = vals.Outflow;
                        inV = vals.Inflow;
                    }

                    props["outflow"] = outV;
                    props["inflow"] = inV;
                }
                return jNode.ToJsonString();
            });

            // 3. 推送给前端地图刷新
            OnGeoJsonReadyToSend?.Invoke(modifiedJson);
            StatusMessage = $"时间切片: {SelectedDate} {SelectedHour}:00 | 有流量网格数: {flowDict.Count}";
        }

    }
}