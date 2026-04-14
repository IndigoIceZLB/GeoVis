// Views/MainWindow.xaml.cs
using GeoVis.Services;
using GeoVis.ViewModels;
using ScottPlot;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;

namespace GeoVis.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // 绑定 ViewModel
            var vm = new MainViewModel();
            this.DataContext = vm;

            // 监听 ViewModel 的数据变化，一旦有数据就开始画图
            vm.PropertyChanged += ViewModel_PropertyChanged;

            // 【新增这一行】：监听 ViewModel 发出的 JSON 字符串，并通过 WebView2 投递给 JS
            vm.OnGeoJsonReadyToSend += jsonStr =>
            {
                // 判空保护：如果浏览器内核还没初始化完成，直接丢弃消息，防止崩溃！
                if (MapWebView != null && MapWebView.CoreWebView2 != null)
                {
                    MapWebView.CoreWebView2.PostWebMessageAsJson(jsonStr);
                }
            };

            // 初始化一个空的坐标系样式
            InitializeChartStyle();

            // 初始化内嵌浏览器引擎
            InitializeWebViewAsync();
        }

        private void InitializeChartStyle()
        {
            MainChart.Plot.Clear();
            MainChart.Plot.Title("网络结构指标逐时演化");

            // 隐藏右侧和顶部的边框，符合学术极简风
            MainChart.Plot.Axes.Right.IsVisible = true;

            MainChart.Refresh();
        }

        private async void InitializeWebViewAsync()
        {
            await MapWebView.EnsureCoreWebView2Async(null);
            string baseDir = System.AppDomain.CurrentDomain.BaseDirectory;
            string htmlPath = Path.Combine(baseDir, "Assets", "web", "map_template.html");

            if (File.Exists(htmlPath))
            {
                MapWebView.CoreWebView2.Navigate(htmlPath);

                // 【新增】：监听 JS 传回来的点击消息
                MapWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            }
            else
            {
                MessageBox.Show("找不到地图模板文件...");
            }
        }

        // 处理来自 JS 的点击事件
        private void CoreWebView2_WebMessageReceived(object sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                // 【核心修复 2】：使用 WebMessageAsJson 属性才能正确接收 JS postMessage 过来的对象！
                string json = e.WebMessageAsJson;

                var payload = System.Text.Json.JsonDocument.Parse(json).RootElement;
                if (payload.TryGetProperty("type", out var typeProp))
                {
                    string type = typeProp.GetString();
                    var vm = this.DataContext as MainViewModel;
                    if (vm == null) return;

                    if (type == "grid_clicked")
                    {
                        // 【致命Bug修复】：使用 ToString() 兼容数字类型的 ID，再也不会崩溃了！
                        string cid = payload.GetProperty("cid").ToString();
                        vm.SelectedGridIdForFlows = cid; // 触发 ViewModel 重新查询该网格关联的 Top 10
                    }
                    else if (type == "map_bg_clicked")
                    {
                        // 用户点击了地图空白处，恢复全局 Top 10
                        vm.SelectedGridIdForFlows = null;
                    }
                }
            }
            catch (Exception ex)
            {
                // 打印出错误，防止未来再发生静默异常
                System.Diagnostics.Debug.WriteLine($"JS 通信解析失败: {ex.Message}");
            }
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            var vm = (MainViewModel)sender;

            // 【修改】：当网络指标数据 OR 驻留指标数据 OR 选中的图表模式 发生变化时，都触发图表重绘
            if (e.PropertyName == nameof(MainViewModel.NetworkMetricsData) ||
                e.PropertyName == nameof(MainViewModel.MobilityMetricsData) ||
                e.PropertyName == nameof(MainViewModel.SelectedChartMode) ||
                    e.PropertyName == nameof(MainViewModel.TemperatureMultiData)) // 【增加气温触发器】
            {
                RenderAcademicChart(vm);
            }
            // 监听锁定状态的切换
            else if (e.PropertyName == nameof(MainViewModel.IsYAxisLocked))
            {
                MainChart.Plot.Axes.Rules.Clear(); // 每次切换先清空规则
                if (vm.IsYAxisLocked)
                {
                    // 动态遍历所有Y轴（主轴、副轴、降水倒挂轴）并加上纵向锁定规则
                    foreach (var yAxis in MainChart.Plot.Axes.GetAxes().OfType<ScottPlot.IYAxis>())
                    {
                        MainChart.Plot.Axes.Rules.Add(new ScottPlot.AxisRules.LockedVertical(yAxis, yAxis.Min, yAxis.Max));
                    }
                }
                MainChart.Refresh();
            }

            if (e.PropertyName == nameof(MainViewModel.HabitChartData))
            {
                RenderHabitShiftChart(vm);
            }
        }

        private void RenderAcademicChart(MainViewModel vm)
        {
            // ====== 【新增代码：步骤 1】记录重绘前的 X 轴视野 ======
            bool hasOldLimits = MainChart.Plot.GetPlottables().Any(); // 判断画布上之前有没有图
            double oldXMin = 0, oldXMax = 0;
            if (hasOldLimits)
            {
                oldXMin = MainChart.Plot.Axes.Bottom.Min;
                oldXMax = MainChart.Plot.Axes.Bottom.Max;
            }

            MainChart.Reset(); // 彻底清理旧画布
            MainChart.Plot.Axes.Rules.Clear(); // 画新图时必须清空约束规则，否则会导致无法自动缩放！

            bool isOdMode = vm.SelectedChartMode == "OD 轨迹流量";

            // --- 1. 准备主线数据 ---
            int dataCount = isOdMode ? (vm.NetworkMetricsData?.Count ?? 0) : (vm.MobilityMetricsData?.Count ?? 0);
            if (dataCount == 0) return;

            double[] xs = new double[dataCount];
            double[] y_primary = new double[dataCount];
            double[] y_secondary = new double[dataCount];

            for (int i = 0; i < dataCount; i++)
            {
                if (isOdMode)
                {
                    var d = vm.NetworkMetricsData[i];
                    DateTime dt = DateTime.ParseExact($"{d.StartDate}{d.StartHour:D2}", "yyyyMMddHH", System.Globalization.CultureInfo.InvariantCulture);
                    xs[i] = dt.ToOADate();
                    y_primary[i] = d.TotalWeight;
                    y_secondary[i] = d.EdgeNumber;
                }
                else
                {
                    var m = vm.MobilityMetricsData[i];
                    DateTime dt = Convert.ToDateTime(m.DateStr).AddHours(m.Hour);
                    xs[i] = dt.ToOADate();
                    y_primary[i] = m.TotalSignal;
                }
            }

            // --- 2. 绘制主线 (左轴) ---
            var sigPrimary = MainChart.Plot.Add.ScatterLine(xs, y_primary);
            sigPrimary.Color = Color.FromHex("#C06C84");
            sigPrimary.LineWidth = 2.5f;
            sigPrimary.Label = isOdMode ? "Total Flow (OD)" : "Total Signal (Mobility)";

            MainChart.Plot.Axes.Left.Label.Text = sigPrimary.Label;
            MainChart.Plot.Axes.Left.Label.ForeColor = sigPrimary.Color;
            MainChart.Plot.Axes.SetLimitsY(0, y_primary.Max() * 1.5, MainChart.Plot.Axes.Left);

            // --- 3. 绘制副线 (右轴) ---
            if (isOdMode)
            {
                var sigSec = MainChart.Plot.Add.ScatterLine(xs, y_secondary);
                sigSec.Color = Color.FromHex("#5890AD");
                sigSec.LineWidth = 2.5f;
                sigSec.Label = "Edge Count";
                sigSec.Axes.YAxis = MainChart.Plot.Axes.Right;

                MainChart.Plot.Axes.Right.Label.Text = "Edge Count";
                MainChart.Plot.Axes.Right.Label.ForeColor = sigSec.Color;
                MainChart.Plot.Axes.SetLimitsY(0, y_secondary.Max() * 1.5, MainChart.Plot.Axes.Right);
            }
            else
            {
                MainChart.Plot.Axes.Right.IsVisible = false;
            }

            // --- 4. 完美倒挂降水柱状图 ---
            if (vm.RainfallMultiData != null && vm.RainfallMultiData.Any())
            {
                var yAxisRain = MainChart.Plot.Axes.AddLeftAxis();
                yAxisRain.Label.Text = "Rainfall (mm)";
                yAxisRain.Label.ForeColor = Color.FromHex("#4FC1E9");

                // 【修复1：刻度精度】：避免放大时出现 9, 9, 9...
                yAxisRain.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic()
                {
                    LabelFormatter = val => Math.Abs(val).ToString("0.##")
                };

                double globalMaxRain = 1;
                string[] palette = { "#4A4FC1E9", "#4AED5565", "#4AA0D468", "#4AFFCE54", "#4AAC92EC" };
                int colorIdx = 0;

                foreach (var kvp in vm.RainfallMultiData)
                {
                    string stationId = kvp.Key; // 此时字典的 Key 已经是没有乱码的 station_id 了
                    var rainDict = kvp.Value;
                    double[] y_rain = new double[dataCount];

                    for (int i = 0; i < dataCount; i++)
                    {
                        DateTime currentDt = DateTime.FromOADate(xs[i]);
                        if (rainDict.TryGetValue(currentDt, out double rVal))
                        {
                            y_rain[i] = -rVal;
                            if (rVal > globalMaxRain) globalMaxRain = rVal;
                        }
                    }

                    var sigRain = MainChart.Plot.Add.Bars(xs, y_rain);
                    string colorHex = palette[colorIdx % palette.Length];

                    foreach (var bar in sigRain.Bars)
                    {
                        bar.FillColor = Color.FromHex(colorHex);
                        bar.Size = 1.0 / 24.0;
                        bar.LineColor = Colors.Transparent;
                    }
                    sigRain.Label = stationId == "Average" ? "Avg Rainfall" : stationId; // 图例直接用ID
                    sigRain.Axes.YAxis = yAxisRain;
                    colorIdx++;
                }
               
                // 【修复2：上移柱状图】：乘数由 4 改为 8，使柱状图纵向大幅收缩，紧贴图表顶部
                MainChart.Plot.Axes.SetLimitsY(-globalMaxRain * 8, 0, yAxisRain);
            }

            // --- 5. 全新科学升级：气温包络带与曲线 (完全独立) ---
            if (vm.SelectedTempStation != null && !vm.SelectedTempStation.Contains("NONE") &&
                vm.TemperatureMultiData != null && vm.TemperatureMultiData.Any())
            {
                var yAxisTemp = MainChart.Plot.Axes.AddRightAxis();
                yAxisTemp.Label.Text = "Temperature (°C)";
                yAxisTemp.Label.ForeColor = Color.FromHex("#E65100"); // 学术深橙色

                // 情况 A：全叠加模式 -> 渲染【平均线 + Min-Max 包络阴影区】
                if (vm.SelectedTempStation == "ALL STATIONS (Overlay 全叠加)")
                {
                    // DuckDB 已经帮我们算好了 Min, Max, Avg 三个虚拟站点
                    var dict = vm.TemperatureMultiData;
                    if (dict.ContainsKey("Min") && dict.ContainsKey("Max") && dict.ContainsKey("Avg"))
                    {
                        var minDict = dict["Min"];
                        var maxDict = dict["Max"];
                        var avgDict = dict["Avg"];

                        List<double> validXs = new();
                        List<double> validMins = new();
                        List<double> validMaxs = new();
                        List<double> validAvgs = new();

                        for (int i = 0; i < dataCount; i++)
                        {
                            DateTime currentDt = DateTime.FromOADate(xs[i]);
                            // 必须三个值同时存在才绘制，确保包络带连续
                            if (minDict.ContainsKey(currentDt) && maxDict.ContainsKey(currentDt) && avgDict.ContainsKey(currentDt))
                            {
                                validXs.Add(xs[i]);
                                validMins.Add(minDict[currentDt]);
                                validMaxs.Add(maxDict[currentDt]);
                                validAvgs.Add(avgDict[currentDt]);
                            }
                        }

                        if (validXs.Any())
                        {
                            // 1. 画阴影包络带 (Fill) - 使用原生强类型RGBA确保透明度生效
                            var band = MainChart.Plot.Add.FillY(validXs.ToArray(), validMins.ToArray(), validMaxs.ToArray());
                            // R=255, G=143, B=0 (深橙色), Alpha=60 (约25%透明度)
                            band.FillColor = new ScottPlot.Color(255, 143, 0, 60);
                            band.LineWidth = 0;
                            band.Label = "Temp Range (Min-Max)";
                            band.Axes.YAxis = yAxisTemp;

                            // 2. 画平均气温主线
                            var lineAvg = MainChart.Plot.Add.ScatterLine(validXs.ToArray(), validAvgs.ToArray());
                            lineAvg.Color = new ScottPlot.Color(230, 81, 0); // 更深的橙色 #E65100
                            lineAvg.LineWidth = 2.5f;
                            lineAvg.Label = "City Avg Temp";
                            lineAvg.Axes.YAxis = yAxisTemp;
                        }
                    }
                }
                // 情况 B：单站点或简单平均模式 -> 渲染常规单线
                else
                {
                    string[] tempPalette = { "#FF8F00", "#FF5252", "#E040FB", "#00B0FF" };
                    int tColorIdx = 0;

                    foreach (var kvp in vm.TemperatureMultiData)
                    {
                        string stationId = kvp.Key;
                        var tempDict = kvp.Value;

                        List<double> validXs = new();
                        List<double> validYs = new();

                        for (int i = 0; i < dataCount; i++)
                        {
                            DateTime currentDt = DateTime.FromOADate(xs[i]);
                            if (tempDict.TryGetValue(currentDt, out double tVal))
                            {
                                validXs.Add(xs[i]);
                                validYs.Add(tVal);
                            }
                        }

                        if (validXs.Any())
                        {
                            var sigTemp = MainChart.Plot.Add.ScatterLine(validXs.ToArray(), validYs.ToArray());
                            sigTemp.Color = Color.FromHex(tempPalette[tColorIdx % tempPalette.Length]);
                            sigTemp.LineWidth = 2.0f;
                            sigTemp.Label = stationId == "Average" ? "Avg Temp" : stationId + " (Temp)";
                            sigTemp.Axes.YAxis = yAxisTemp;
                            tColorIdx++;
                        }
                    }
                }
            }

            // --- 6. 画布刷新与乱码修复 ---
            MainChart.Plot.Axes.DateTimeTicksBottom();
            MainChart.Plot.Axes.Bottom.Label.Text = "Date & Hour";

            var legend = MainChart.Plot.ShowLegend(Alignment.UpperCenter);
            legend.Orientation = Orientation.Horizontal;
            legend.FontName = "Microsoft YaHei"; // 保险起见保留字体设置

            // ====== 【修改代码：步骤 2】恢复重绘前的 X 轴视野 ======
            if (hasOldLimits && !double.IsNaN(oldXMin) && !double.IsNaN(oldXMax) && oldXMin != oldXMax)
            {
                // 如果之前有视野记录，无缝恢复到之前的拖拽位置！
                MainChart.Plot.Axes.SetLimitsX(oldXMin, oldXMax);
            }
            else
            {
                // 如果是软件刚打开第一次画图，默认显示前 72 小时的跨度
                MainChart.Plot.Axes.SetLimitsX(xs[0], xs[Math.Min(72, dataCount - 1)]);
            }


            // 【新增逻辑】：在完成了最佳比例缩放后，如果此时系统处于"锁定"状态，自动把 Y 轴焊死！
            if (vm.IsYAxisLocked)
            {
                foreach (var yAxis in MainChart.Plot.Axes.GetAxes().OfType<ScottPlot.IYAxis>())
                {
                    MainChart.Plot.Axes.Rules.Add(new ScottPlot.AxisRules.LockedVertical(yAxis, yAxis.Min, yAxis.Max));
                }
            }

            MainChart.Refresh();
        }

        // [Method: 渲染出行习惯分析主图表 - 完整且包含所有分支]
        private void RenderHabitShiftChart(MainViewModel vm)
        {
            if (vm.HabitChartData is not List<DataQueryService.HabitShiftShare> data || !data.Any()) return;

            HabitMainChart.Reset();

            var englishLabels = new Dictionary<string, string>
    {
        { "1_提前2小时及以上", "Early >=2h" }, { "2_提前1小时", "Early 1h" },
        { "3_按时出行", "On time" }, { "4_推迟1小时", "Delay 1h" },
        { "5_推迟2小时及以上", "Delay >=2h" }, { "6_消失未出行", "Missing" }
    };
            var shiftColors = new Dictionary<string, ScottPlot.Color>
    {
        { "1_提前2小时及以上", ScottPlot.Color.FromHex("#4575b4") }, { "2_提前1小时", ScottPlot.Color.FromHex("#91bfdb") },
        { "3_按时出行", ScottPlot.Color.FromHex("#66bd63") }, { "4_推迟1小时", ScottPlot.Color.FromHex("#fdae61") },
        { "5_推迟2小时及以上", ScottPlot.Color.FromHex("#d73027") }, { "6_消失未出行", ScottPlot.Color.FromHex("#8c8c8c") }
    };

            var shiftTypes = data.Select(x => x.ShiftType).Distinct().OrderBy(x => x).ToList();

            if (vm.SelectedHabitChartMode.Contains("单日全天"))
                HabitMainChart.Plot.Title("Hourly Composition of Habit Shifts (Single Day)");
            else
                HabitMainChart.Plot.Title("Cross-day Habit Shift Trend");

            // ==========================================
            // 模式 1：多日时段横向对比 (全新！堆叠柱状)
            // ==========================================
            if (vm.SelectedHabitChartMode == "多日时段横向对比 (堆叠柱状)")
            {
                var dates = data.Select(x => x.DateStr).Distinct().OrderBy(x => x).ToList();
                int numDates = dates.Count;
                double[] xs = Enumerable.Range(0, numDates).Select(x => (double)x).ToArray();
                double[] bottoms = new double[numDates];

                foreach (var type in shiftTypes)
                {
                    double[] ys = new double[numDates];
                    for (int d = 0; d < numDates; d++)
                    {
                        var row = data.FirstOrDefault(x => x.ShiftType == type && x.DateStr == dates[d]);
                        ys[d] = row != null ? row.SharePct : 0;
                    }

                    var bar = HabitMainChart.Plot.Add.Bars(xs, ys);
                    bar.Color = shiftColors.ContainsKey(type) ? shiftColors[type] : Colors.Gray;
                    bar.Label = englishLabels.ContainsKey(type) ? englishLabels[type] : type;

                    for (int i = 0; i < numDates; i++)
                    {
                        bar.Bars[i].ValueBase = bottoms[i];
                        bottoms[i] += ys[i];
                        bar.Bars[i].Value = bottoms[i];
                        bar.Bars[i].Size = 0.6; // 控制柱子粗细
                    }
                }
                ScottPlot.Tick[] ticks = new ScottPlot.Tick[numDates];
                for (int i = 0; i < numDates; i++) ticks[i] = new ScottPlot.Tick(i, dates[i].Length > 5 ? dates[i].Substring(5) : dates[i]);
                HabitMainChart.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(ticks);
                HabitMainChart.Plot.Axes.Bottom.Label.Text = "Date";
                HabitMainChart.Plot.Axes.Left.Label.Text = $"Share in {vm.SelectedHabitPeriod} (%)";
                HabitMainChart.Plot.Axes.SetLimits(-0.6, numDates - 0.4, 0, 100);
            }
            // ==========================================
            // 模式 2：特定小时横向对比 (分组柱状)
            // ==========================================
            else if (vm.SelectedHabitChartMode == "特定小时横向对比 (分组柱状)")
            {
                var dates = data.Select(x => x.DateStr).Distinct().OrderBy(x => x).ToList();
                int numDates = dates.Count;
                int numTypes = shiftTypes.Count;
                double[] xs = Enumerable.Range(0, numDates).Select(x => (double)x).ToArray();
                double groupWidth = 0.8; double barWidth = groupWidth / numTypes;

                for (int i = 0; i < numTypes; i++)
                {
                    string type = shiftTypes[i];
                    double[] ys = new double[numDates];
                    for (int d = 0; d < numDates; d++)
                    {
                        var row = data.FirstOrDefault(x => x.ShiftType == type && x.DateStr == dates[d]);
                        ys[d] = row != null ? row.SharePct : 0;
                    }
                    double[] shiftedXs = xs.Select(x => x - groupWidth / 2 + (i + 0.5) * barWidth).ToArray();
                    var bar = HabitMainChart.Plot.Add.Bars(shiftedXs, ys);
                    bar.Color = shiftColors.ContainsKey(type) ? shiftColors[type] : Colors.Gray;
                    bar.Label = englishLabels.ContainsKey(type) ? englishLabels[type] : type;
                    foreach (var b in bar.Bars) b.Size = barWidth * 0.85;
                }

                ScottPlot.Tick[] ticks = new ScottPlot.Tick[numDates];
                for (int i = 0; i < numDates; i++) ticks[i] = new ScottPlot.Tick(i, dates[i].Length > 5 ? dates[i].Substring(5) : dates[i]);
                HabitMainChart.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(ticks);
                HabitMainChart.Plot.Axes.Bottom.Label.Text = "Date";
                HabitMainChart.Plot.Axes.Left.Label.Text = "Share (%)";
                HabitMainChart.Plot.Axes.SetLimits(-0.5, numDates - 0.5, 0, null);
            }
            // ==========================================
            // 模式 3：逐时占比 (单日全天堆叠图)
            // ==========================================
            else if (vm.SelectedHabitChartMode == "逐时占比 (单日全天堆叠图)")
            {
                int hours = 24;
                double[] xs = Enumerable.Range(0, hours).Select(x => (double)x).ToArray();
                double[] bottoms = new double[hours];

                foreach (var type in shiftTypes)
                {
                    double[] ys = new double[hours];
                    var typeData = data.Where(x => x.ShiftType == type).ToList();
                    foreach (var d in typeData) ys[d.HabitHour] = d.SharePct;

                    var bar = HabitMainChart.Plot.Add.Bars(xs, ys);
                    bar.Color = shiftColors.ContainsKey(type) ? shiftColors[type] : Colors.Gray;
                    bar.Label = englishLabels.ContainsKey(type) ? englishLabels[type] : type;

                    for (int i = 0; i < hours; i++)
                    {
                        bar.Bars[i].ValueBase = bottoms[i];
                        bottoms[i] += ys[i];
                        bar.Bars[i].Value = bottoms[i];
                    }
                }
                HabitMainChart.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic();
                HabitMainChart.Plot.Axes.Bottom.Label.Text = "Habit Hour (0-23)";
                HabitMainChart.Plot.Axes.Left.Label.Text = "Share Percentage (%)";
                HabitMainChart.Plot.Axes.SetLimits(-0.5, 23.5, 0, 100);
            }
            // ==========================================
            // 模式 4：多日时段趋势 (折线图)
            // ==========================================
            else if (vm.SelectedHabitChartMode == "多日时段趋势 (折线图)")
            {
                var dates = data.Select(x => x.DateStr).Distinct().OrderBy(x => x).ToList();
                int numDates = dates.Count;
                double[] xs = Enumerable.Range(0, numDates).Select(x => (double)x).ToArray();

                foreach (var type in shiftTypes)
                {
                    double[] ys = new double[numDates];
                    for (int d = 0; d < numDates; d++)
                    {
                        var row = data.FirstOrDefault(x => x.ShiftType == type && x.DateStr == dates[d]);
                        ys[d] = row != null ? row.SharePct : 0;
                    }

                    var scatter = HabitMainChart.Plot.Add.Scatter(xs, ys);
                    scatter.Color = shiftColors.ContainsKey(type) ? shiftColors[type] : Colors.Gray;
                    scatter.Label = englishLabels.ContainsKey(type) ? englishLabels[type] : type;
                    scatter.LineWidth = 2.5f;
                    scatter.MarkerSize = 7;
                }

                ScottPlot.Tick[] ticks = new ScottPlot.Tick[numDates];
                for (int i = 0; i < numDates; i++) ticks[i] = new ScottPlot.Tick(i, dates[i].Length >= 10 ? dates[i].Substring(5) : dates[i]);
                HabitMainChart.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(ticks);
                HabitMainChart.Plot.Axes.Bottom.Label.Text = "Date";
                HabitMainChart.Plot.Axes.Left.Label.Text = $"Share in {vm.SelectedHabitPeriod} (%)";
                HabitMainChart.Plot.Axes.SetLimits(-0.5, numDates - 0.5, 0, null);
            }

            // 绘制图例
            HabitMainChart.Plot.ShowLegend(ScottPlot.Alignment.UpperRight);

            // 【全新逻辑】：因为下方降水图对齐需要上方画布彻底稳定，强行执行一次布局重置
            HabitMainChart.Refresh();

            // ===== 渲染底部的全宽降水图 =====
            if (vm.ShowHabitRainfall)
            {
                RenderFloatingRainfallChart(vm, data);
            }
        }

        // [Method: 彻底修复版 - 动态全对齐降水图表]
        private void RenderFloatingRainfallChart(MainViewModel vm, List<DataQueryService.HabitShiftShare> habitData)
        {
            HabitRainfallChart.Reset();
            if (vm.RainfallMultiData == null || !vm.RainfallMultiData.Any()) return;

            string uiStation = vm.SelectedHabitRainfallStation ?? "Average";

            // 提取目前图表上存在的独立日期数组
            var targetDates = habitData.Select(x => x.DateStr).Distinct().OrderBy(x => x).ToList();
            int numDates = targetDates.Count;
            if (numDates == 0) return;

            // 【终极修复】：处理“全叠加”和“特定站点”的字典提取逻辑
            List<string> stationsToDraw = new();
            if (uiStation == "ALL STATIONS (Overlay 全叠加)")
            {
                stationsToDraw = vm.RainfallMultiData.Keys.ToList();
            }
            else if (uiStation == "ALL STATIONS (Average 平均)")
            {
                if (vm.RainfallMultiData.ContainsKey("Average")) stationsToDraw.Add("Average");
            }
            else
            {
                // 【核心修复】：如果是单一站点，由于底层查询方法只返回该站点的结果
                // 所以字典里必定只有 1 个 Key。我们不管这个 Key 叫 S001 还是什么，直接全取出来！
                if (vm.RainfallMultiData.Keys.Any())
                {
                    stationsToDraw.Add(vm.RainfallMultiData.Keys.First());
                }
            }

            if (!stationsToDraw.Any()) return;

            // 颜色轮盘，全叠加时用
            string[] palette = { "#4FC1E9", "#ED5565", "#A0D468", "#FFCE54", "#AC92EC", "#48CFAD" };
            int colorIdx = 0;

            foreach (var stKey in stationsToDraw)
            {
                var rainDict = vm.RainfallMultiData[stKey];
                List<double> xs = new();
                List<double> ys = new();

                // 模式 A：单日全天堆叠图 (0-23 X轴)
                if (vm.SelectedHabitChartMode.Contains("单日全天"))
                {
                    string singleDate = DateTime.ParseExact(vm.HabitSelectedSingleDate.ToString(), "yyyyMMdd", null).ToString("yyyy-MM-dd");
                    for (int h = 0; h < 24; h++)
                    {
                        var matchTime = rainDict.Keys.FirstOrDefault(k => k.ToString("yyyy-MM-dd") == singleDate && k.Hour == h);
                        xs.Add(h);
                        ys.Add(matchTime != default ? rainDict[matchTime] : 0);
                    }
                    HabitRainfallChart.Plot.Axes.SetLimitsX(-0.5, 23.5);
                    HabitRainfallChart.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic();
                    HabitRainfallChart.Plot.Axes.Bottom.Label.Text = "Hour of Day";
                }
                else // 模式 B：跨日统计 (X轴对应 0, 1, 2...)
                {
                    for (int d = 0; d < numDates; d++)
                    {
                        string dateStr = targetDates[d];
                        double rainSum = 0;

                        if (vm.SelectedHabitChartMode.Contains("特定小时横向对比"))
                        {
                            int h = vm.SelectedHabitHour;
                            var matchTime = rainDict.Keys.FirstOrDefault(k => k.ToString("yyyy-MM-dd") == dateStr && k.Hour == h);
                            if (matchTime != default) rainSum = rainDict[matchTime];
                        }
                        else
                        {
                            // 时段趋势 或 时段堆叠 的区间提取
                            List<int> validHours = vm.SelectedHabitPeriod switch
                            {
                                string p when p.Contains("06-09") => new() { 6, 7, 8, 9 },
                                string p when p.Contains("10-16") => new() { 10, 11, 12, 13, 14, 15, 16 },
                                string p when p.Contains("17-20") => new() { 17, 18, 19, 20 },
                                string p when p.Contains("00-05") => new() { 0, 1, 2, 3, 4, 5 },
                                _ => new() { 21, 22, 23 }
                            };
                            rainSum = rainDict.Where(k => k.Key.ToString("yyyy-MM-dd") == dateStr && validHours.Contains(k.Key.Hour)).Sum(k => k.Value);
                        }
                        xs.Add(d);
                        ys.Add(rainSum);
                    }

                    HabitRainfallChart.Plot.Axes.SetLimitsX(-0.6, numDates - 0.4);
                    ScottPlot.Tick[] ticks = new ScottPlot.Tick[numDates];
                    for (int i = 0; i < numDates; i++) ticks[i] = new ScottPlot.Tick(i, targetDates[i].Length > 5 ? targetDates[i].Substring(5) : targetDates[i]);
                    HabitRainfallChart.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(ticks);
                    HabitRainfallChart.Plot.Axes.Bottom.Label.Text = "Date";
                }

                if (xs.Any())
                {
                    var bars = HabitRainfallChart.Plot.Add.Bars(xs.ToArray(), ys.ToArray());
                    // 如果是全叠加模式，加入 50% 的透明度以方便辨认
                    if (uiStation == "ALL STATIONS (Overlay 全叠加)")
                    {
                        var hex = palette[colorIdx % palette.Length];
                        bars.Color = ScottPlot.Color.FromHex(hex).WithAlpha(120);
                    }
                    else
                    {
                        bars.Color = ScottPlot.Color.FromHex("#4FC1E9");
                    }

                    foreach (var b in bars.Bars) b.Size = 0.6;
                }
                colorIdx++;
            }

            HabitRainfallChart.Plot.Axes.Bottom.TickLabelStyle.FontSize = 11;
            HabitRainfallChart.Plot.Axes.Left.Label.Text = "Rain (mm)";
            HabitRainfallChart.Refresh();
        }

        // ==========================================
        // 【新增功能】：鼠标悬浮数据探针解析引擎
        // ==========================================
        private void HabitMainChart_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // 鼠标移出画布，隐藏信息板
            HabitTooltipBorder.Visibility = Visibility.Collapsed;
        }

        private void HabitMainChart_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            var vm = this.DataContext as MainViewModel;
            if (vm?.HabitChartData is not List<DataQueryService.HabitShiftShare> data || !data.Any()) return;

            // 1. 获取鼠标像素并逆向映射为图表上的笛卡尔坐标 (X, Y)
            var mousePos = e.GetPosition(HabitMainChart);
            ScottPlot.Pixel mousePixel = new ScottPlot.Pixel((float)mousePos.X, (float)mousePos.Y);
            ScottPlot.Coordinates mouseLocation = HabitMainChart.Plot.GetCoordinates(mousePixel);

            // 2. 将浮点 X 坐标四舍五入，寻找最近的柱子索引
            int targetX = (int)Math.Round(mouseLocation.X);

            var englishLabels = new Dictionary<string, string>
    {
        { "1_提前2小时及以上", "Early >=2h" }, { "2_提前1小时", "Early 1h" },
        { "3_按时出行", "On time" }, { "4_推迟1小时", "Delay 1h" },
        { "5_推迟2小时及以上", "Delay >=2h" }, { "6_消失未出行", "Missing" }
    };

            string tooltipMsg = "";

            // 3. 根据当前模式解析 X 轴代表的含义
            if (vm.SelectedHabitChartMode.Contains("单日全天"))
            {
                // 模式 A：X 代表 0-23 小时
                if (targetX < 0 || targetX > 23) { HabitTooltipBorder.Visibility = Visibility.Collapsed; return; }

                var hourData = data.Where(x => x.HabitHour == targetX).OrderBy(x => x.ShiftType).ToList();
                if (!hourData.Any()) { HabitTooltipBorder.Visibility = Visibility.Collapsed; return; }

                tooltipMsg += $"⏱️ Habit Hour: {targetX:D2}:00\n";
                tooltipMsg += "----------------------\n";
                foreach (var d in hourData)
                    tooltipMsg += $"[{englishLabels[d.ShiftType]}]: {d.SharePct:F2}%\n";
            }
            else
            {
                // 模式 B：X 代表日期数组的索引
                var dates = data.Select(x => x.DateStr).Distinct().OrderBy(x => x).ToList();
                if (targetX < 0 || targetX >= dates.Count) { HabitTooltipBorder.Visibility = Visibility.Collapsed; return; }

                string targetDate = dates[targetX];
                var dayData = data.Where(x => x.DateStr == targetDate).OrderBy(x => x.ShiftType).ToList();
                if (!dayData.Any()) { HabitTooltipBorder.Visibility = Visibility.Collapsed; return; }

                tooltipMsg += $"📅 Date: {targetDate}\n";
                tooltipMsg += "----------------------\n";
                foreach (var d in dayData)
                    tooltipMsg += $"[{englishLabels[d.ShiftType]}]: {d.SharePct:F2}%\n";
            }

            // 4. 更新 UI 显示
            HabitTooltipText.Text = tooltipMsg.TrimEnd('\n');
            HabitTooltipBorder.Visibility = Visibility.Visible;
        }

    }
}