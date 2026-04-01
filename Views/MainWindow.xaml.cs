// Views/MainWindow.xaml.cs
using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using GeoVis.ViewModels;
using ScottPlot;
using System.IO;

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
            vm.OnGeoJsonReadyToSend += jsonStr => MapWebView.CoreWebView2.PostWebMessageAsJson(jsonStr);

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
            // 等待 WebView2 核心组件初始化完毕
            await MapWebView.EnsureCoreWebView2Async(null);

            // 获取本地 HTML 文件的绝对路径
            string baseDir = System.AppDomain.CurrentDomain.BaseDirectory;
            string htmlPath = Path.Combine(baseDir, "Assets", "web", "map_template.html");

            if (File.Exists(htmlPath))
            {
                // 导航加载本地 HTML 文件
                MapWebView.CoreWebView2.Navigate(htmlPath);
            }
            else
            {
                MessageBox.Show("找不到地图模板文件，请检查 Assets/web/map_template.html 的生成操作是否设为了‘如果较新则复制’。");
            }
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            var vm = (MainViewModel)sender;

            // 【修改】：当网络指标数据 OR 驻留指标数据 OR 选中的图表模式 发生变化时，都触发图表重绘
            if (e.PropertyName == nameof(MainViewModel.NetworkMetricsData) ||
                e.PropertyName == nameof(MainViewModel.MobilityMetricsData) ||
                e.PropertyName == nameof(MainViewModel.SelectedChartMode))
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

            // --- 5. 画布刷新与乱码修复 ---
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
       
    }
}