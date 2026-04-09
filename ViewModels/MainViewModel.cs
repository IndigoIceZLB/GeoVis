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
        private int _selectedDate;
        public int SelectedDate
        {
            get => _selectedDate;
            set
            {
                if (SetProperty(ref _selectedDate, value))
                {
                    _ = UpdateMapByTimeAsync();
                    _ = UpdateMapRainfallAsync(); // 同步更新右下角降水图表
                }
            }
        }
        [ObservableProperty] private List<int> _availableDates;

        private int _selectedHour = 8;
        public int SelectedHour
        {
            get => _selectedHour;
            set
            {
                if (SetProperty(ref _selectedHour, value))
                {
                    _ = UpdateMapByTimeAsync();
                    _ = UpdateMapRainfallAsync(); // 拖动滑块时，图表也跟着变！
                }
            }
        }

        [ObservableProperty] private List<string> _availableStations;
        [ObservableProperty] private string _selectedStation;

        [ObservableProperty] private List<string> _chartModes = new() { "OD 轨迹流量", "网格驻留人口" };
        [ObservableProperty] private string _selectedChartMode = "OD 轨迹流量";

        [ObservableProperty] private List<DataQueryService.HourlyMobilityMetric> _mobilityMetricsData;

        // 我们需要把降水数据也暴露给 UI 画图用
        // 气象数据类型变更为嵌套字典
        [ObservableProperty] private Dictionary<string, Dictionary<DateTime, double>> _rainfallMultiData;

        [ObservableProperty] private List<string> _availableTempStations;
        [ObservableProperty] private string _selectedTempStation;
        [ObservableProperty] private Dictionary<string, Dictionary<DateTime, double>> _temperatureMultiData;

        [ObservableProperty] private List<string> _mapModes = new() { "OD 轨迹流向", "网格驻留人口", "月度常住人口" };
        private string _selectedMapMode = "OD 轨迹流向";
        public string SelectedMapMode
        {
            get => _selectedMapMode;
            set { if (SetProperty(ref _selectedMapMode, value)) _ = UpdateMapByTimeAsync(); }
        }

        [ObservableProperty] private List<string> _analysisModes = new() { "绝对值分布", "时空环比做差" };
        private string _selectedAnalysisMode = "绝对值分布";
        public string SelectedAnalysisMode
        {
            get => _selectedAnalysisMode;
            set { if (SetProperty(ref _selectedAnalysisMode, value)) _ = UpdateMapByTimeAsync(); }
        }

        [ObservableProperty] private List<string> _odDisplayModes = new() { "总流量", "流出流量", "流入流量" };
        private string _selectedOdDisplayMode = "总流量";
        public string SelectedOdDisplayMode
        {
            get => _selectedOdDisplayMode;
            set { if (SetProperty(ref _selectedOdDisplayMode, value)) _ = UpdateMapByTimeAsync(); }
        }
        

        // 【新增】：用于控制Y轴锁定的属性
        [ObservableProperty] private bool _isYAxisLocked = false;

        // 【新增】：是否开启 Top 10 飞线渲染
        private bool _showTopFlows = false;
        public bool ShowTopFlows
        {
            get => _showTopFlows;
            set { if (SetProperty(ref _showTopFlows, value)) _ = UpdateMapByTimeAsync(); }
        }

        // 【新增】：是否剔除自流（O=D 的格网内流动）
        private bool _excludeIntraZonalFlow = true;
        public bool ExcludeIntraZonalFlow
        {
            get => _excludeIntraZonalFlow;
            set { if (SetProperty(ref _excludeIntraZonalFlow, value)) _ = UpdateMapByTimeAsync(); }
        }

        // 【新增】：用于控制飞线的增减趋势显示
        [ObservableProperty]
        private List<string> _topFlowTrendModes = new() { "变化绝对值最大", "仅看激增 (红线)", "仅看锐减 (蓝线)" };

        private string _selectedTopFlowTrendMode = "变化绝对值最大";
        public string SelectedTopFlowTrendMode
        {
            get => _selectedTopFlowTrendMode;
            set { if (SetProperty(ref _selectedTopFlowTrendMode, value)) _ = UpdateMapByTimeAsync(); }
        }

        // 【完全掌控生命周期的显式属性】
        private string _selectedGridIdForFlows = null;
        public string SelectedGridIdForFlows
        {
            get => _selectedGridIdForFlows;
            set
            {
                if (SetProperty(ref _selectedGridIdForFlows, value))
                {
                    _ = UpdateMapByTimeAsync(); // 只要被赋值，强制去查最新飞线并刷地图！
                }
            }
        }

        [ObservableProperty] private List<string> _diffModes = new() { "与上一小时做差", "与选定日期同时间做差" };
        private string _selectedDiffMode = "与上一小时做差";
        public string SelectedDiffMode
        {
            get => _selectedDiffMode;
            set { if (SetProperty(ref _selectedDiffMode, value)) _ = UpdateMapByTimeAsync(); }
        }

        // 【新增】：支持多选的做差基准日期集合
        [ObservableProperty] private System.Collections.ObjectModel.ObservableCollection<DateSelectionItem> _referenceDates = new();

        [ObservableProperty] private System.Collections.ObjectModel.ObservableCollection<StationSelectionItem> _mapStations = new();
        private bool _showMapRainfall = false;
        public bool ShowMapRainfall
        {
            get => _showMapRainfall;
            set
            {
                if (SetProperty(ref _showMapRainfall, value))
                {
                    // 勾选或取消勾选时，立即向前端发送显示/隐藏指令
                    _ = UpdateMapRainfallAsync();
                }
            }
        }

        // 【新增】：一键全选气象站功能
        private bool _isAllStationsSelected = false;
        public bool IsAllStationsSelected
        {
            get => _isAllStationsSelected;
            set
            {
                if (SetProperty(ref _isAllStationsSelected, value))
                {
                    // 遍历所有站点，强制设为全选/取消全选状态
                    foreach (var s in MapStations) s.IsSelected = value;
                    // 主动触发一次数据库查询与图表重绘
                    _ = UpdateMapRainfallAsync();
                }
            }
        }

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
                        "Temperature" => "temperature_data",
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
            if (string.IsNullOrEmpty(value) || value.Contains("NONE"))
            {
                RainfallMultiData?.Clear();
                OnPropertyChanged(nameof(NetworkMetricsData)); // 触发清空重绘
            }
            else
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

                        // 【新增】：在这里填充基准日期的多选列表！
                        ReferenceDates.Clear();
                        foreach (var d in dates)
                        {
                            var item = new DateSelectionItem { DateValue = d, DisplayDate = d.ToString(), IsSelected = false };
                            item.OnSelectionChanged = () => _ = UpdateMapByTimeAsync(); // 绑定勾选刷新事件
                            ReferenceDates.Add(item);
                        }
                        if (ReferenceDates.Any()) ReferenceDates.First().IsSelected = true; // 默认勾选第一个
                    }
                    // 3. 触发一次渲染.注释掉下面这行，避免瞬间触发两次渲染卡死浏览器！
                    //await UpdateMapByTimeAsync();
                }
                catch (Exception ex) { StatusMessage = $"渲染失败: {ex.Message}"; }
            }
        }

        // 核心：根据当前选中的时间和小时，重新计算并推送 JSON
        private async Task UpdateMapByTimeAsync()
        {
            if (string.IsNullOrEmpty(_baseGeoJson) || SelectedDate == 0) return;

            // 【提取所有打勾的基准日期】
            var selectedRefDates = ReferenceDates.Where(x => x.IsSelected).Select(x => x.DateValue).ToList();

            // 1. 获取网格颜色的底层数据
            var flowDict = await _dataService.GetSpatialDataAsync(
                SelectedMapMode, SelectedAnalysisMode, SelectedOdDisplayMode,
                SelectedDate, SelectedHour, SelectedDiffMode, selectedRefDates);

            // ====== 【第 2 步新增：获取 Top 10 飞线数据】 ======
            List<DataQueryService.OdFlowLine> topFlows = new();
            if (SelectedMapMode == "OD 轨迹流向" && ShowTopFlows)
            {
                topFlows = await _dataService.GetTopOdFlowLinesAsync(
                    SelectedAnalysisMode, SelectedDate, SelectedHour,
                    SelectedDiffMode, selectedRefDates,
                    SelectedGridIdForFlows, 10, ExcludeIntraZonalFlow,
                    SelectedOdDisplayMode,
                    SelectedTopFlowTrendMode); // 【新增】：把趋势模式传给底层
            }
            // ====================================================

            string modifiedJson = await Task.Run(() =>
            {
                var jNode = JsonNode.Parse(_baseGeoJson);
                jNode["map_mode"] = SelectedMapMode;
                jNode["od_display_mode"] = SelectedOdDisplayMode;
                jNode["analysis_mode"] = SelectedAnalysisMode;

                // ====== 【第 2 步新增：把飞线数组塞进 JSON 根节点】 ======
                var flowsArray = new System.Text.Json.Nodes.JsonArray();
                foreach (var f in topFlows)
                {
                    var fNode = new System.Text.Json.Nodes.JsonObject
                    {
                        ["o_grid"] = f.OGrid,
                        ["d_grid"] = f.DGrid,
                        ["diff_val"] = f.DiffVal
                    };
                    flowsArray.Add(fNode);
                }
                jNode["top_flows"] = flowsArray;
                // ==========================================================

                // 遍历每一个多边形网格，赋颜色和数值（你原有的逻辑保持不变）
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
                var tempStations = await _dataService.GetTemperatureStationsAsync();
                if (tempStations.Any()) AvailableTempStations = tempStations;

                var stations = await _dataService.GetRainfallStationsAsync();
                if (stations.Any()) AvailableStations = stations;

                var metrics = await _dataService.GetHourlyNetworkMetricsAsync();
                if (metrics != null && metrics.Any()) NetworkMetricsData = metrics.ToList();

                var mobility = await _dataService.GetHourlyMobilityMetricsAsync();
                if (mobility != null && mobility.Any()) MobilityMetricsData = mobility;

                OnPropertyChanged(nameof(SelectedChartMode)); // 触发一次画图


                // 初始化气象站复选框列表 (剔除关闭和全选选项)
                if (stations.Any())
                {
                    MapStations.Clear();
                    foreach (var s in stations.Skip(3)) MapStations.Add(new StationSelectionItem { StationId = s, IsSelected = false });
                   
                }
            }
            catch { }
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
                    "Temperature" => "temperature_data",
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
                if (dataType == "Temperature") { AvailableTempStations = new(); TemperatureMultiData?.Clear(); }

                OnPropertyChanged(nameof(NetworkMetricsData));
            }
        }

        [RelayCommand] // 气象站复选框点击后触发此命令
        private async Task UpdateMapRainfallAsync()
        {
            // 如果没开启或者没选日期，发送隐藏图表的指令
            if (!ShowMapRainfall || SelectedDate == 0)
            {
                var emptyPayload = new { type = "rainfall_chart", show = false };
                OnGeoJsonReadyToSend?.Invoke(System.Text.Json.JsonSerializer.Serialize(emptyPayload));
                return;
            }

            var selectedIds = MapStations.Where(x => x.IsSelected).Select(x => x.StationId).ToList();
            var data = await _dataService.GetMapRainfallAsync(selectedIds, SelectedDate, SelectedHour);

            // 构建发给 ECharts 的动态 JSON
            var chartPayload = new
            {
                type = "rainfall_chart",
                show = true,
                time = $"{SelectedHour:D2}:00",
                stations = data.Keys.ToList(),
                values = data.Values.ToList()
            };

            // 直接通过已有的高频通道发给浏览器
            OnGeoJsonReadyToSend?.Invoke(System.Text.Json.JsonSerializer.Serialize(chartPayload));
        }

        // 监听气温站点选择变化
        partial void OnSelectedTempStationChanged(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Contains("NONE"))
            {
                TemperatureMultiData?.Clear();
                OnPropertyChanged(nameof(NetworkMetricsData)); // 触发清空重绘
            }
            else
            {
                _ = LoadTemperatureForChartAsync(value);
            }
        }

        private async Task LoadTemperatureForChartAsync(string station)
        {
            TemperatureMultiData = await _dataService.GetTemperatureMultiDataAsync(station);
            // 巧妙复用：触发 NetworkMetricsData 通知，即可驱动 MainWindow.xaml.cs 重新绘制整个图表
            OnPropertyChanged(nameof(NetworkMetricsData));
        }
    }

    public partial class StationSelectionItem : ObservableObject
    {
        [ObservableProperty] private string _stationId;
        [ObservableProperty] private bool _isSelected;
    }

    public partial class DateSelectionItem : ObservableObject
    {
        public int DateValue { get; set; }
        public string DisplayDate { get; set; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            // 当用户勾选/取消勾选某个日期时，立即触发地图重新做差
            set { if (SetProperty(ref _isSelected, value)) OnSelectionChanged?.Invoke(); }
        }
        public Action OnSelectionChanged { get; set; }
    }


}