// ====== 【新增：ViewModels/DualMapViewModel.cs】 ======
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GeoVis.Services;
using Microsoft.Win32;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace GeoVis.ViewModels
{
    public partial class DualMapViewModel : ObservableObject
    {
        private readonly DataQueryService _dataService;

        [ObservableProperty] private string _statusMessage = "等待加载基础 GeoJSON 网格...";
        [ObservableProperty] private bool _isRightMapVisible = true;

        
        private string _selectedDimension = "Home (居住地)";
        public string SelectedDimension
        {
            get => _selectedDimension;
            set { if (SetProperty(ref _selectedDimension, value)) _ = RenderMapsAsync(); }
        }

        // 1. 在属性定义区，追加一个“住职比”维度
        [ObservableProperty] private System.Collections.ObjectModel.ObservableCollection<string> _dimensions = new() { "Home (居住地)", "Work (工作地)", "住职比 (Home/Work Ratio)" };

        private string _baseGeoJson = null;
        public event Action<string> OnGeoJsonReadyToSend;

        public DualMapViewModel()
        {
            _dataService = new DataQueryService();
        }

        partial void OnIsRightMapVisibleChanged(bool value)
        {
            var payload = new { type = "toggle_right", show = value };
            OnGeoJsonReadyToSend?.Invoke(JsonSerializer.Serialize(payload));
        }

        [RelayCommand]
        private async Task LoadGeoJsonAsync()
        {
            var ofd = new OpenFileDialog { Filter = "GeoJSON|*.geojson", Title = "请选择用于渲染的底层网格 GeoJSON" };
            if (ofd.ShowDialog() == true)
            {
                StatusMessage = "解析网格中...";
                _baseGeoJson = await Task.Run(() => File.ReadAllText(ofd.FileName));
                await RenderMapsAsync();
            }
        }

        private async Task RenderMapsAsync()
        {
            if (string.IsNullOrEmpty(_baseGeoJson)) return;

            StatusMessage = "DuckDB 高并发计算中...";
            
            object activeData;
            object inactiveData;
            string payloadDimension;

            if (SelectedDimension.Contains("Ratio"))
            {
                payloadDimension = "Ratio";
                // 并发获取住职比复杂对象
                var taskActive = _dataService.GetHabitSpatialRatioAsync("active_spatial_data");
                var taskInactive = _dataService.GetHabitSpatialRatioAsync("inactive_spatial_data");
                await Task.WhenAll(taskActive, taskInactive);
                activeData = taskActive.Result;
                inactiveData = taskInactive.Result;
            }
            else
            {
                payloadDimension = SelectedDimension.Contains("Home") ? "Home" : "Work";
                int ptype = payloadDimension == "Home" ? 1 : 2;
                // 并发获取单维度绝对值
                var taskActive = _dataService.GetHabitSpatialDataAsync("active_spatial_data", ptype);
                var taskInactive = _dataService.GetHabitSpatialDataAsync("inactive_spatial_data", ptype);
                await Task.WhenAll(taskActive, taskInactive);
                activeData = taskActive.Result;
                inactiveData = taskInactive.Result;
            }

            // 组装 JSON，因为 activeData 是 object（可能是 Dictionary<string, long> 或 Dictionary<string, HabitRatioData>），
            // System.Text.Json 会利用反射完美将其序列化发送给 JS。
            var payload = new
            {
                type = "render",
                dimension = payloadDimension,
                geoJson = JsonNode.Parse(_baseGeoJson),
                activeData = activeData,
                inactiveData = inactiveData
            };

            string jsonStr = JsonSerializer.Serialize(payload);
            OnGeoJsonReadyToSend?.Invoke(jsonStr);

            StatusMessage = $"渲染完成 | 维度: {SelectedDimension}";
        }
    }
}