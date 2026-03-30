// ViewModels/MainViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GeoVis.DataAccess;
using GeoVis.Models;
using GeoVis.Services;
using Microsoft.Win32; // 用于弹窗选择文件
using System;
using System.Collections.Generic;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes; // .NET 的极速 JSON 处理库
using System.Threading.Tasks;
using System.Windows; // 确保顶部有这个引用用于弹窗

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

        [ObservableProperty] private List<string> _availableStations;
        [ObservableProperty] private string _selectedStation;

        [ObservableProperty] private List<string> _chartModes = new() { "OD 轨迹流量", "网格驻留人口" };
        [ObservableProperty] private string _selectedChartMode = "OD 轨迹流量";

        [ObservableProperty] private List<DataQueryService.HourlyMobilityMetric> _mobilityMetricsData;

        // 我们需要把降水数据也暴露给 UI 画图用
        // 气象数据类型变更为嵌套字典
        [ObservableProperty] private Dictionary<string, Dictionary<DateTime, double>> _rainfallMultiData;

        [ObservableProperty] private List<string> _mapModes = new() { "OD 流出与流入", "驻留人口与变化 (做差)", "月度常住人口" };
        [ObservableProperty] private string _selectedMapMode = "OD 流出与流入";


        // 用于在内存中缓存基础的 GeoJSON 字符串，避免频繁读盘
        private string _baseGeoJson = null;

        public MainViewModel()
        {
            _dataService = new DataQueryService();
            StatusMessage = GeoVis.DataAccess.DuckDbFactory.GetDatabaseInfo();

            // 【新增】：软件一打开，就去本地数据库尝试拉取数据，实现持久化秒开
            _ = AutoLoadDataFromDatabaseAsync();
        }

        // --- 新增通用导入命令 ---
        [RelayCommand]
        private async Task ImportMultiDataAsync(string dataType)
        {
            var ofd = new OpenFileDialog { Filter = "CSV 文件 (*.csv)|*.csv", Title = $"选择 {dataType} 数据" };
            if (ofd.ShowDialog() == true)
            {
                StatusMessage = $"正在导入 {dataType}...";
                try
                {
                    string tableName = dataType switch
                    {
                        "OD" => "od_data",
                        "Rainfall" => "rainfall_data",
                        "Mobility" => "mobility_data",
                        "Population" => "pop_data",
                        _ => "temp_data"
                    };

                    var (rows, ms) = await _dataService.ImportTableAsync(tableName, ofd.FileName);
                    StatusMessage = $"【{dataType}】导入成功: {rows:N0} 行, 耗时 {ms}ms | {DuckDbFactory.GetDatabaseInfo()}";

                    // 【修复】：导入完成后，自动刷新图表和站点！
                    await AutoLoadDataFromDatabaseAsync();
                }
                catch (Exception ex) { StatusMessage = $"导入失败: {ex.Message}"; }
            }
        }

        // 监听站点选择变化
        partial void OnSelectedStationChanged(string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                _ = LoadRainfallForChartAsync(value);
            }
        }

        private async Task LoadRainfallForChartAsync(string station)
        {
            // 极速获取降水字典并赋值给属性，触发图表重绘
            RainfallMultiData = await _dataService.GetRainfallMultiDataAsync(station);

            // 为了让图表一起更新，我们主动触发一下 NetworkMetricsData 的重绘逻辑
            // 这样 MainWindow.xaml.cs 就能把 OD 和 降水画在同一张图上
            OnPropertyChanged(nameof(NetworkMetricsData));
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

            // 1. 调用新的全能统一查询
            var flowDict = await _dataService.GetSpatialDataAsync(SelectedMapMode, SelectedDate, SelectedHour);

            string modifiedJson = await Task.Run(() =>
            {
                var jNode = JsonNode.Parse(_baseGeoJson);

                // 【核心】：把当前图层模式告诉前端 JS！
                jNode["map_mode"] = SelectedMapMode;

                foreach (var feature in jNode["features"].AsArray())
                {
                    var props = feature["properties"];
                    string cid = props["cid"]?.ToString();
                    long v1 = 0, v2 = 0;
                    if (flowDict.TryGetValue(cid, out var vals)) { v1 = vals.Val1; v2 = vals.Val2; }
                    props["val1"] = v1;
                    props["val2"] = v2;
                }
                return jNode.ToJsonString();
            });

            OnGeoJsonReadyToSend?.Invoke(modifiedJson);
            StatusMessage = $"地图渲染: {SelectedMapMode} | 有数据网格数: {flowDict.Count}";
        }

        private async Task AutoLoadDataFromDatabaseAsync()
        {
            try
            {
                var stations = await _dataService.GetRainfallStationsAsync();
                if (stations.Any()) AvailableStations = stations;

                var metrics = await _dataService.GetHourlyNetworkMetricsAsync();
                if (metrics != null && metrics.Any()) NetworkMetricsData = metrics.ToList();

                var mobility = await _dataService.GetHourlyMobilityMetricsAsync();
                if (mobility != null && mobility.Any()) MobilityMetricsData = mobility;

                OnPropertyChanged(nameof(SelectedChartMode)); // 触发一次画图
            }
            catch { }
        }

        // 监听分析模式切换
        partial void OnSelectedChartModeChanged(string value)
        {
            OnPropertyChanged(nameof(NetworkMetricsData)); // 借用这个属性变更来通知 UI 重绘
        }

        // 监听图层切换，瞬间重绘地图
        partial void OnSelectedMapModeChanged(string value)
        {
            _ = UpdateMapByTimeAsync();
        }

        [RelayCommand]
        private async Task ClearTableAsync(string dataType)
        {
            var result = MessageBox.Show($"确定要清空【{dataType}】数据吗？\n清空后可重新导入，此操作不可逆。",
                                         "危险操作确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                string tableName = dataType switch
                {
                    "OD" => "od_data",
                    "Rainfall" => "rainfall_data",
                    "Mobility" => "mobility_data",
                    "Population" => "pop_data",
                    _ => "temp_data"
                };

                await _dataService.ClearTableAsync(tableName);
                StatusMessage = $"【{dataType}】已成功清空 | {DuckDbFactory.GetDatabaseInfo()}";

                // 清空内存并刷新图表
                if (dataType == "Rainfall") { AvailableStations = new(); RainfallMultiData?.Clear(); }
                if (dataType == "OD") { NetworkMetricsData?.Clear(); }
                if (dataType == "Mobility") { MobilityMetricsData?.Clear(); }

                OnPropertyChanged(nameof(NetworkMetricsData));
            }
        }
    }
}