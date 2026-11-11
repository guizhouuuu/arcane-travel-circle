# 审核说明

当前版本：1.2.1。

## 关键实现

- `Services/CircleRegistry.cs`：通过 SMAPI 内容事件注册 `Data/Buildings` 和贴图。价格、建造、扣款、放置、移动和拆除均沿用原版建筑系统；没有额外手工扣款逻辑。
- `Services/MapViewAdapter.cs`：识别正在显示的原版地图或其子类。读取实际地图画布，排除关闭按钮、页签及子菜单。
- `Services/MapTargetResolver.cs`：按已生效的世界地图数据反算场景/地块，处理小矩形优先、等优先级歧义拒绝、图标的室外出口及原版进度门槛。
- `Services/SafeLandingFinder.cs`：有限半径内按距离搜索，检查真实人物到达姿态、地图与物体阻挡、立即触发的传送格及可离开性。
- `ModEntry.cs`：邻接右键开图或站阵手动开图，临传送再次检查来源和距离，通过正常 `Game1.warpFarmer` 流程加载场景。
- `MiniCircleService` / `MiniCircleLedger`：法师卡片直接购买、单次扣款/失败回滚、限购与许可防复制、放置拾取、SaveData 和地面绘制。
- `TravelNetwork` / `NetworkMapOverlay`：大小阵节点、标记避让与跨区域侧栏。小阵只选节点，大阵保留普通地图点击；侧栏空白不会穿透。

- `ScepterTravelEffect.cs`：对照本机1.6.15的`Tools.Wand.DoFunction`、`wandWarpForReal`和`DelayedAction.ApplyFade`复现12个354粒子、17个6号横向精灵、wand音效、1秒延时及原版淡出；不用Wand硬编码的农场目的地。单人范围使用本地temporarySprites，不广播联网效果。传送前复核场景、来源、节点存在、节日替换及安全落点；异常/读档/事件恢复显示、移动、freezePause及原有无敌状态，失效回调用代次隔离。

## 补丁与兼容

使用两个范围受限的Harmony补丁：

- `CarpenterMenu.receiveLeftClick(int,int,bool)`前缀：仅接管Wizard中本Mod小阵卡片的购买按钮，执行直接交易并跳过原版建造。一次只生成一个唯一许可，扣20万金；余额不足、前置不足、限购或背包满不保留扣款/许可。失败退款使用原版addUnearnedMoney，避免退款增加终身收入。购买成功留在本卡片，没有子商店。
- `CarpenterMenu.performHoverAction(int,int)`后缀：仅将上述按钮的私有hoverText改成当前语言的“购买”。字段及签名对本机程序集核对；其他卡片、按钮和地图均不接管。

原版鼠标、键盘生成点击和手柄确认均经过同一个点击方法；防重入及300ms去重避免连发购买。没有修改NPC Map Locations DLL或地图页，NPC依赖继续为可选。

安全碰撞检查有一处受版本约束的只读反射：读取 Mountain 的滑坡/铁路阻挡、Beach 的老水手、IslandSouth 的海龟阻挡状态和碰撞矩形。原因是原版部分碰撞重载会丢弃 `skipCollisionEffects` 参数，直接调用可能产生碰撞副作用；另一些六参数重载的特殊障碍又不会由十参数重载覆盖。所有字段名称、实例属性和类型已对本机 1.6.15 DLL 验证，并加入离线断言。升级游戏后应重新核对这些字段。

实际 NPC Map Locations 3.5.2 的 `ModMapPage` 继承 `MapPage`，继承其左键处理，使用同样的地图边界和区域数据。已核对实际 DLL；标记显示、悬停和各种设置下的共同运行效果仍需游戏内验收。

## 已处理的审核风险

- 原版 `mapBounds` 原点已是 UI 坐标，宽高仍需乘四；输入统一使用 `Game1.getMousePosition(true)`，避免游戏缩放与 UI 缩放混淆。
- 不调用会修改共享滚动文字缓存的 `MapAreaPosition.GetScrollText` 解析悬停地点名。
- 沙漠固定点标记不能反推地块，因此仅对匹配原版特征的条目使用明确记录的区域近似。
- 夜市等被动节日可能替换目标地图，节庆当天的活动场景也会改写原版传送流程；本 Mod 对这些目标保守拒绝。
- 原版会调整巴士站、姜岛南部和最右列的部分传送坐标；候选必须与最终原版落点一致。
- 不消费其他流程预留的 `nextFarmerWarpOffsetX/Y`，存在偏移时不激活。
- 1.1.0 可离开性识别所有真实传送阵；小阵放置后自身即为出口。静态碰撞区分裸水和有明确可走甲板的水面。
- 小阵物品使用独立内部 Name 防止原版合堆丢失许可，显示名仍为本地化名称和编号；`not_placeable` 和公开 `CanBeSetDown=false` 阻止原版放置产生阻挡物。
- 独立代码审核后修复绘图回调首次加载 PNG、覆盖层遮住软件鼠标、手持小阵吞工具栏点击和联机农场工人读档报错。PNG 在 SaveLoaded 预加载；覆盖层最后重画鼠标；HUD 点击保留原版处理；不支持的联机模式禁用小阵读写。
- 可离开性通过有限 BFS 证明：连通到可用室外出口、沙漠返程巴士或当前源法阵；无法证实出口的封闭组件拒绝，达到 16,384 节点上限也保守拒绝。
- 临时状态不写入存档；关闭地图、离阵、读档、传送和事件会撤销激活。
- 无效点击原因同时显示于地图上的短提示，避免 HUD 被菜单遮住。

## 验证边界

构建和离线断言使用本机真实游戏程序集。没有通过伪造同名游戏类或 stub 冒充编译成功。仅核对源码和程序集的事项不标为游戏实测。

精确的已测/未测项目与构建结果以 [TESTING.md](TESTING.md) 为准。

## 1.2.0 沙滩拒绝原因与回归

实际`Data/WorldMap`在普通Beach之外，全年还列出无Condition的BeachNightMarket，二者PixelArea同为(186,137,57,31)。1.1.0把不支持的夜市映射记成同等优先级的阻挡层，导致普通Beach候选也被否决。现在仅排除匹配原版特征的这层替代地图；没有放宽水面、碰撞、进度或节日保护。ElliottHouse原位置矩形(211,140,2,3)扩至原版ElliottCabin提示框(209,137,6,8)，通过室内实际warps取唯一Beach门外落点。

新增回归使用真实XNB创建无图形Game1上下文并调用完整TryResolve，包含两层重叠数据、小屋边缘、多个沙滩点击、画布移动及真实出口连通。另在真实CarpenterMenu方法执行Harmony补丁，并验证取消传送后人物状态恢复。无启动游戏、无读写真实存档。

## 1.2.1：未访问深山的真实碰撞回归

Mountain构造器依据当时的DaysPlayed初始化landslide/railroadAreaBlocked，之后在resetSharedState中分别于第5/31天清除。读档后未访问深山时，检查远端场景可以读到创建时的旧阻挡状态；旧安全检查会据此封死公会一侧的出口。同时已修复的采石场桥要在MakeMapModifications后才有完整桥面。之前只检查原始沙地/甲板或没有动态状态的场景，无法发现此问题。

现在SafeLandingFinder对Mountain先调用公开且幂等的MakeMapModifications，不执行resetForPlayerEntry/resetSharedState、不触发入场事件；特殊碰撞检查按实际DaysPlayed预测原版进入后的阻挡状态，不写NetBool、不增加解锁进度。未到第5/31天继续拒绝对应阻挡，未完成ccCraftsRoom仍不能到桥东。

新增回归在真实Mountain和Farmer上模拟“第0天创建场景、第100天读档进度、未进入目标”，加载真实地图、灌木、树和WorldMap，执行完整解析、原生碰撞及出口搜索。同一回归对原1.2.0程序集失败，对新版通过。无启动游戏或写真实存档。
