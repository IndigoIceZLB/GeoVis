# GeoVis
# GeoResearchVis: High-Performance Spatiotemporal Data Analysis & Visualization Desktop

![Platform](https://img.shields.io/badge/Platform-Windows%20(WPF)-blue.svg)
![Framework](https://img.shields.io/badge/.NET-8.0-purple.svg)
![Database](https://img.shields.io/badge/Engine-DuckDB-yellow.svg)
![License](https://img.shields.io/badge/License-MIT-green.svg)

**GeoResearchVis** 是一款专为城市规划、交通地理与空间计算领域的科研人员打造的**轻量级、100%纯离线、高性能**空间数据分析桌面端软件。

本项目旨在彻底解决传统基于 Python (Pandas/GeoPandas) 脚本在处理千万级（10M+）OD（起讫点）轨迹流与网格信令数据时存在的**内存溢出、计算缓慢、缺乏连贯交互**等痛点。依托内置的列式 OLAP 数据库引擎与多线程异步渲染架构，GeoResearchVis 能够在普通单机设备上实现**毫秒级的数据切片、多源异构数据（人类活动与气象）联合分析以及学术级图表的极速渲染**。

---

## 🌟 核心特性 (Core Features)

1. **极速的本地分析引擎 (In-process OLAP Engine)**
   - 集成 **DuckDB** 作为本地计算核心，直接对 GB 级 CSV 长表进行列式索引建库。
   - 抛弃低效的内存驻留模式，利用 SQL 进行底层的分组聚合（Group By）、空间做差与多表联合（Join），**千万级记录的单次交互查询耗时 < 0.2 秒**。
2. **多源异构数据融合与学术级时序图表 (Multidimensional Temporal Analysis)**
   - 采用纯 C# 高性能计算图表库 **ScottPlot 5**，支持百万点级别的无极缩放。
   - **多轴叠加与降水倒挂 (Hyetograph)**：支持在同一时间轴上叠加 OD 流量、拓扑边数量，并将小时级气象站降水数据以“倒挂柱状图（透明层叠）”形式展现，直观揭示极端天气对城市出行的微观影响。
3. **动态空间交互与时空切片映射 (Dynamic Spatiotemporal Mapping)**
   - 利用 **WebView2 + Leaflet.js** 架构，实现后台高强度计算与前端轻量级拓扑渲染的解耦。
   - **双向与差分渲染引擎**：不仅支持红黄热力图渲染，更内置了基于 SQL 的**环比差分模型**。在时间切片滑块（0-23小时）拖动时，瞬间计算并渲染网格的冷暖色差分（红增蓝减），动态捕捉城市早晚高峰的“呼吸”与潮汐现象。
4. **100% 离线与数据安全 (Fully Offline)**
   - 不依赖任何云端计算资源，地图底图资源、JS 组件库及业务数据全量本地化，满足涉密科研项目的数据合规要求。

---

## 🏗️ 技术架构 (Architecture)

本系统采用严格的 **MVVM (Model-View-ViewModel)** 架构设计，确保了视图层与业务计算层的彻底分离。

- **展现层 (UI)**: WPF (.NET 8) / 原生极简学术风样式。
- **视图模型层 (ViewModel)**: `CommunityToolkit.Mvvm` (基于 Source Generators 实现零样板代码的双向绑定)。
- **数据访问层 (DAL)**: `DuckDB.NET` + `Dapper` (实现极速的数据读取与对象映射，规避 EF Core 的臃肿)。
- **渲染桥接层 (Rendering Bridge)**: 
  - **图表侧**: C# 数组直通 SkiaSharp (ScottPlot) 绘图上下文。
  - **空间侧**: C# 在内存中组装并注入计算结果至 GeoJSON 属性节点，通过 WebView2 的 `PostWebMessageAsJson` 高速通道传递给内嵌浏览器引擎解析。

---

## 📊 数据规范与准备 (Data Schema Requirements)

在导入数据前，请确保您的原始 CSV 数据符合以下宽转长（Flatten）规范格式：

1. **OD 轨迹流长表 (`od_data`)**: 需包含 `start_date`, `start_hour`, `end_hour`, `o_grid`, `d_grid`, `trip_cnt`。
2. **网格驻留信号密度表 (`mobility_data`)**: 需包含 `date`, `hour`, `grid_id`, `signal_count`。
3. **网格常住人口表 (`pop_data`)**: 需包含 `grid_id`, `real_pop_sum`, `unicom_cnt`。
4. **小时气象降水极简表 (`rainfall_data`)**: 需包含 `timestamp`, `station`, `precip_mm`。
5. **空间网格底图 (`.geojson`)**: 标准的 WGS84 坐标系 GeoJSON，`properties` 中需包含用于 Join 的主键 `cid`。

---

## 🚀 快速开始与使用指南 (Usage Guide)

### 1. 环境编译
- 使用 **Visual Studio 2022** 打开 `GeoResearchVis.sln`。
- 确保已安装 `.NET 8.0 Desktop Development` 工作负载。
- 将 `Assets/web/map_template.html` 的文件属性设置为 **“如果较新则复制” (Copy if newer)**。
- 还原 NuGet 包并编译运行 (`F5`)。

### 2. 操作工作流

#### Step 1: 数据建库 (Database Ingestion)
1. 在左侧面板的 **“1. 数据建库 (CSV入表)”** 区域，依次点击对应按钮导入准备好的 CSV 文件。
2. 软件会在执行目录下生成持久化的 `.duckdb` 文件。**该操作仅需执行一次**，后续打开软件将实现数据“秒开”。
3. *(注：如发现数据存在预处理错误，可点击旁边的红色“清空”按钮物理删除该表，再重新导入)*。

#### Step 2: 网络与气象时序统计 (Temporal Analysis)
1. 切换至右侧的 **“📈 网络与气象时序统计”** 选项卡。
2. 顶部工具栏可选择**分析指标**（OD 轨迹流量 / 网格驻留人口）。
3. 选择叠加的**气象站数据**（支持单站点查询、全站点叠加 Overlay 或全域平均 Average）。
4. 使用鼠标滚轮或右键平移，可无极缩放与审视局部的时序细节。

#### Step 3: 空间时间切片与差分渲染 (Spatiotemporal Spatial Analysis)
1. 在左侧面板点击 **“加载网格(GeoJSON)渲染”**，载入对应的城市网格数据。
2. 切换至右侧的 **“🗺️ 空间底图呈现”** 选项卡。
3. 在左下角的 **“空间时间切片控制”** 面板中，选择特定**日期**与**渲染图层**（例如：驻留人口与变化）。
4. **拖动 0-23 小时的滑动条**，地图将根据 DuckDB 在后台实时执行的 SQL 做差计算结果，瞬间更新网格的红蓝冷暖色，并在鼠标悬浮时提供精确的流入/流出指标 Tooltip。

---

## 🛡️ 常见问题 (FAQ)

- **Q: 为什么图表标签全为英文？**
  A: 为了确保不同 Windows 系统环境下 SkiaSharp 渲染引擎的兼容性，并符合国际一流学术期刊的出图规范，本项目已移除了图表渲染层的中文字体依赖，统一采用英文坐标标签。
  
- **Q: 导入海量数据时软件会卡顿吗？**
  A: 导入过程采用 `Task.Run` 异步封装以及 DuckDB 原生 `read_csv_auto` 算法，百万行级别数据通常在 1 秒内完成入库，期间 UI 不会发生阻塞。

---

## 📜 许可证 (License)

本项目采用 [MIT License](LICENSE) 开源协议。欢迎学术界同仁基于此架构进行二次开发与衍生研究。

*如在学术论文中使用了本软件架构生成的可视化图表，欢迎在致谢中提及本工具。*