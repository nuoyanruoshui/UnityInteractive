# Unity Interactive — Unity 交互系统框架

## 概述

基于 Unity EventSystem + InputSystem 的交互框架。提供拖拽（Drag）、焦点（Focus）、选择（Click）、长按（Long Press）四种交互类型，通过特性驱动的 **交互案例（InteractCase）** 系统解耦 Subject-Target 交互逻辑，配合 LRU 调度和独立的协程长按检测器，实现高性能、可扩展的 UI 交互方案。

### 核心特性

- 🖱️ **四种交互接口** — `IDraggable` / `IFocusable` / `ISelectable` / `ILongPressHandler`，统一派生自 `IInteractive`
- ⚡ **自包含长按检测** — 每个组件实例独立协程计时器，按下时才运行，无按下时零 Update 开销
- 🔒 **拖拽与长按互斥** — Drag 触发后自动取消长按计时，Press 期间屏蔽拖拽，同一时刻互不干扰
- 🔌 **InputSystem 无关** — `EasyInput` 自动检测 Touchscreen / Mouse 设备，带 Legacy Input fallback
- 🧩 **特性驱动案例系统** — `[InteractCase]` 注册，反射自动发现，支持 Drag 路径和 LongPress 路径
- 📊 **Order 严格优先的调度** — 按 `Order` 升序匹配，同 Order 组内命中案例前移（LRU 不会越过 Order）
- 🔗 **泛型案例基类** — 泛型变体免去 `OnExecute` / `OnEnter` 内手动 `as` 类型转换
- 🛡️ **悬挂引用自动清理** — OnDisable 释放自己占用的槽位，Update 中检测已销毁 / 已失活对象并清除全局引用

---

## 目录结构

```
Assets/Plugins/unity-interactive/
├── Runtime/
│   ├── IInteractive.cs              # 接口定义（IInteractive, IDraggable, IFocusable, ISelectable, ILongPressHandler, IInteractCase, InteractContext）
│   ├── InteractiveComponent.cs      # 交互组件基类（EventSystem 事件 → protected virtual 方法 + 协程长按检测 + Drag/Press 互斥）
│   ├── UnityInteractive.cs          # 全局管理器（Singleton, 案例 LRU 调度, 全局交互状态跟踪, 运行时注册/注销）
│   ├── AbstractInteractCase.cs      # 交互案例基类（AbstractInteractCase, DragSubjectFocusTargetInteractCase, LongPressSubjectFocusTargetInteractCase + 泛型变体, InteractCaseAttribute）
│   └── EasyInput.cs                 # 输入抽象层（InputSystem Touch/Mouse 自动检测 + Legacy Input fallback + EventSystem 射线检测）
└── Editor/
    └── UnityInteractiveEditor.cs    # Inspector 编辑器扩展
```

---

## 快速开始

### 1. 创建可交互组件

继承 `InteractiveComponent`，按需重写对应的虚方法：

```csharp
using NuoYan.Interactive;
using UnityEngine.EventSystems;

public class DraggableItem : InteractiveComponent
{
    protected override void OnStartDrag(PointerEventData eventData) { /* 开始拖拽 */ }
    protected override void OnUpdateDrag(PointerEventData eventData) { /* 拖拽中（每帧） */ }
    protected override void OnStopDrag(PointerEventData eventData) { /* 拖拽结束 */ }

    protected override void OnStartPress(PointerEventData eventData) { /* 达到长按阈值 */ }
    protected override void OnPress(PointerEventData eventData) { /* 长按持续中 */ }
    protected override void OnStopPress(PointerEventData eventData) { /* 长按结束 */ }

    protected override void OnFocus(PointerEventData eventData) { /* 指针进入 */ }
    protected override void OnLostFocus(PointerEventData eventData) { /* 指针离开 */ }
    protected override void OnSelect(PointerEventData eventData) { /* 点击 */ }
}
```

> **注意**：GameObject 上需有 Collider / GraphicRaycaster 等让 EventSystem 能射线命中。

### 2. 启用/禁用交互能力

每个组件实例在 Inspector 中有三个开关，也可代码控制：

```csharp
item.EnableInteractive = false;  // 完全关闭所有交互
item.EnableDrag = false;         // 仅关闭拖拽
item.EnableLongPress = false;    // 仅关闭长按
```

所有虚方法均受对应开关门控，子类无需重复检查。

### 3. 配置全局长按参数

场景中 `UnityInteractive` GameObject 的 Inspector 面板：

| 参数 | 默认值 | 说明 |
|------|--------|------|
| `LongPressThresholdTime` | 1.5s | 按下多久后触发长按 |
| `LongPressInterval` | 1s | 长按触发后 `OnPress` 调用间隔。≤0 表示每帧调用 |

### 4. 定义交互案例

使用 `[InteractCase]` 特性 + 泛型基类声明 Subject 拖拽/长按到 Target 上的行为：

```csharp
using System;
using NuoYan.Interactive;

[InteractCase(typeof(DraggableItem), typeof(DropZone), order: 10)]
public class ItemToSlotCase : DragSubjectFocusTargetInteractCase<DraggableItem, DropZone>
{
    // 反射激活需要 public (Type, Type) 构造函数：
    // 泛型基类的构造函数都是 protected，而构造函数不会被继承，派生类必须自己声明一个。
    public ItemToSlotCase(Type subject, Type target) : base(subject, target) { }

    protected override void OnEnter(DraggableItem item, DropZone zone)
    {
        zone.Highlight(true);
    }

    protected override void OnExecute(DraggableItem item, DropZone zone)
    {
        item.transform.position = zone.transform.position;
    }

    protected override void OnExit()
    {
        // 离开时清理（实例 ID 自动追踪，无需手动记录上次对象）
    }
}
```

> 案例在 `UnityInteractive.InitOnce()` 中通过反射自动发现并注册，无需手动添加。
> 忘记写 `public (Type, Type)` 构造函数时，框架会退化为 public 无参构造 + 特性里的 Subject/Target，
> 并在两者都不可用时 `Debug.LogError` 跳过该案例（不会影响其它案例）。

---

## 架构概览

```
                       ┌──────────────────────────────┐
                       │      UnityInteractive         │
                       │   (Singleton, Update 驱动)     │
                       │                              │
                       │  CurrentDraggable             │
                       │  CurrentFocusable             │
                       │  CurrentLongPress             │
                       │  AllInteractCase (Dictionary) │
                       │  ActiveCases (LRU List)       │
                       └──────────┬───────────────────┘
                                  │ 每帧 Match
                       ┌──────────▼───────────────────┐
                       │   IInteractCase.Execute()     │
                       │  Subject × Target 类型匹配    │
                       │  首个命中即停止，LRU 前移      │
                       └──────────┬───────────────────┘
                                  │
             ┌────────────────────┼──────────────────────┐
             ▼                    ▼                      ▼
      IDraggable             IFocusable          ILongPressHandler
      (BeginDrag/Drag/       (PointerEnter/      (PointerDown + 协程计时
       EndDrag)               PointerExit)        → OnBeginLongPress/
                                                  OnLongPress/
                                                  OnEndLongPress)
             │                    │                      │
             └────────────────────┴──────────────────────┘
                                  │
                       ┌──────────▼───────────────────┐
                       │   InteractiveComponent        │
                       │  EventSystem → protected 虚方法 │
                       │  Drag/Press 互斥 + 协程计时     │
                       │  Enable 开关门控               │
                       └──────────────────────────────┘
```

### 核心机制

| 机制 | 实现 | 说明 |
|------|------|------|
| 长按检测 | `LongPressRoutine()` 协程 | 仅按下时运行，`yield return null` 逐帧累加；无按下时零 Update 开销 |
| Drag/Press 互斥 | `GetInteractState(Drag)` / `GetInteractState(Press)` | Drag 开始时 `CancelLongPress()`；Press 协程中检测到 Drag 则跳过累加 |
| Click 抑制 | `m_SuppressNextClick` | 长按结束后自动抑制紧随的 `OnPointerClick`，避免双击逻辑双触发 |
| 案例匹配 | `Update()` 每帧遍历 | 按 Order 升序，首个 `Execute` 返回 true 即停；命中的案例只在**同一 Order 组内**前移（组内 LRU），Order 的优先级不随运行历史漂移 |
| 悬挂清理 | `OnDisable` + `Update` 开头 | 组件失活时清理协程/状态并释放**自己占用的**全局槽位；Update 开头用 `IsValid` 清除已销毁**或已失活**（`SetActive(false)` / `enabled=false`）的引用 |
| 输入抽象 | `EasyInput` 静态类 | 优先检查 Touchscreen 活跃触摸 → 走触摸路径；否则走 Mouse；均不可用 fallback 到 `Input.GetMouseButton*` |

---

## API 参考

### InteractiveComponent（交互组件基类）

继承自 `MonoBehaviour`，实现 `IDraggable`, `IFocusable`, `ISelectable`, `ILongPressHandler`。

**配置属性（Inspector 可见）：**

| 属性 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `EnableInteractive` | `bool` | `true` | 总开关，关闭后本组件不写全局槽位、不触发任何回调。**注意它不会让组件退出 uGUI 的处理者筛选**（见 FAQ） |
| `EnableDrag` | `bool` | `true` | 拖拽开关（prefab 静态开关；运行时按数据判断请重写 `CanStartDrag`） |
| `EnableLongPress` | `bool` | `true` | 长按开关 |

**可重写虚方法：**

| 方法 | 对应事件 | 触发时机 |
|------|---------|---------|
| `CanStartDrag(PointerEventData)` | — | 拖拽前置判定，返回 `false` 则本次手势不进入拖拽（不注册、无任何 Drag 回调） |
| `OnStartDrag(PointerEventData)` | BeginDrag | 开始拖拽（在此调用 `CancelDrag()` 可否决本次拖拽） |
| `OnUpdateDrag(PointerEventData)` | Drag | 拖拽中（每帧），仅在拖拽被接受后触发 |
| `OnStopDrag(PointerEventData)` | EndDrag | 拖拽结束，仅在拖拽被接受后触发 |
| `OnFocus(PointerEventData)` | PointerEnter | 指针进入 |
| `OnLostFocus(PointerEventData)` | PointerExit | 指针离开 |
| `OnSelect(PointerEventData)` | PointerClick | 点击（长按后自动抑制） |
| `OnStartPress(PointerEventData)` | OnBeginLongPress | 达到 `LongPressThresholdTime`，**仅一次** |
| `OnPress(PointerEventData)` | OnLongPress | 长按持续中，受 `LongPressInterval` 节流 |
| `OnStopPress(PointerEventData)` | OnEndLongPress | 松手、拖拽开始、指针移出、组件失活 |

**运行时否决拖拽：**

```csharp
// 方式一：前置判定（推荐，读数据决定能否拖）
protected override bool CanStartDrag(PointerEventData eventData) => m_Data != null && m_Data.CanDrag;

// 方式二：在 OnStartDrag 内否决
protected override void OnStartDrag(PointerEventData eventData)
{
    if (!CheckSomething()) { CancelDrag(); return; }   // 不会成为 CurrentDraggable，也不会收到 OnUpdateDrag/OnStopDrag
    ...
}
```

**查询方法：**

```csharp
// 检查交互状态（所有 key 均为 true 才返回 true）
bool isDragging = comp.GetInteractState(InteractiveComponent.Drag);
bool isPressing = comp.GetInteractState(InteractiveComponent.Press);
bool isFocused  = comp.GetInteractState(InteractiveComponent.Focus);
```

### UnityInteractive（全局管理器）

Singleton，自动创建 DontDestroyOnLoad GameObject。

**状态查询：**

| 属性 | 类型 | 说明 |
|------|------|------|
| `CurrentDraggable` | `IDraggable` | 当前拖拽中的对象 |
| `CurrentFocusable` | `IFocusable` | 当前指针悬停的对象 |
| `CurrentLongPress` | `ILongPressHandler` | 当前按下中的对象 |
| `CurrentInteractCase` | `IInteractCase` | 当前命中的交互案例 |
| `ActiveInteractCaseOrder` | `IReadOnlyList<IInteractCase>` | 当前匹配顺序快照（下标 0 最先匹配），调试/Editor 用 |
| `InstanceOrNull` | `UnityInteractive`（静态） | 不创建实例的访问入口，供失活/销毁等清理路径使用 |

**案例管理：**

| 方法 | 说明 |
|------|------|
| `RegisterInteractCase(IInteractCase, bool)` | 运行时注册案例 |
| `UnregisterInteractCase<T>()` | 运行时注销案例 |
| `EnableInteractCase<T>()` | 运行时启用案例 |
| `DisableInteractCase<T>()` | 运行时禁用案例 |

**状态设置：**

| 方法 | 说明 |
|------|------|
| `SetCurrentDraggable(IDraggable)` | 设置当前拖拽对象（传 null 清除） |
| `SetCurrentFocusable(IFocusable)` | 设置当前焦点对象 |
| `SetCurrentLongPress(ILongPressHandler)` | 设置当前长按对象 |

### EasyInput（输入抽象）

纯静态方法，无实例化。

**指针状态查询：**

```csharp
// 按下
bool down = EasyInput.PointerDown();
bool down = EasyInput.PointerDown(index, out int fingerId, out Vector2 position);

// 抬起
bool up = EasyInput.PointerUp();
bool up = EasyInput.PointerUp(index, out int fingerId);

// 移动/按住
bool move = EasyInput.PointerMove();
bool move = EasyInput.PointerMove(index, out int fingerId, out Vector2 position);

// 当前指针下的 GameObject（EventSystem 射线检测）
if (EasyInput.TryGetCurrentPointRayCast(out GameObject hit))
{
    // hit 为射线命中的首个 GameObject
}
```

**设备选择逻辑：**

| 条件 | 路径 | 多指支持 |
|------|------|---------|
| `Touchscreen.current` 有活跃触摸 | 触摸路径 | ✓（通过 index 参数） |
| `Mouse.current` 存在 | 鼠标路径 | — |
| 以上均无 | `Input.GetMouseButton*` fallback | — |

### IInteractCase（交互案例接口）

**InteractCaseAttribute 参数：**

| 参数 | 类型 | 说明 |
|------|------|------|
| `subject` | `Type` | Subject 类型（拖拽/长按的主体） |
| `target` | `Type` | Target 类型（焦点目标） |
| `enableExecuteOnLoad` | `bool` | 初始化后是否立即启用，默认 `true` |
| `order` | `int` | 匹配优先级，越小越先，默认 `0` |

**案例基类选择：**

| 基类 | 适用场景 | OnEnter / OnExecute 参数 |
|------|---------|--------------------------|
| `DragSubjectFocusTargetInteractCase` | 仅 Drag × Focus | `IDraggable` / `IFocusable` |
| `DragSubjectFocusTargetInteractCase<TSubject, TTarget>` | 同上 + 泛型 | 强类型 `TSubject` / `TTarget`（推荐） |
| `LongPressSubjectFocusTargetInteractCase` | 仅 LongPress × Focus | `ILongPressHandler` / `IFocusable` |
| `LongPressSubjectFocusTargetInteractCase<TSubject, TTarget>` | 同上 + 泛型 | 强类型 `TSubject` / `TTarget`（推荐） |
| `AbstractInteractCase` | Drag + LongPress 双路径 | 各自 `OnDrag*` / `OnLongPress*` |

---

## 长按检测详解

长按检测完全自包含在 `InteractiveComponent` 内部，不依赖 UnityInteractive 的 Update：

```
OnPointerDown
  │
  ├─ m_Pressing = true, m_PressTime = 0
  ├─ StartCoroutine(LongPressRoutine())    ← 协程启动
  └─ SetCurrentLongPress(this)
        │
        ▼
  LongPressRoutine (yield return null 每帧)
        │
        ├─ PressTime < ThresholdTime  → 继续累加
        ├─ PressTime >= ThresholdTime → OnBeginLongPress (一次)
        │                               + OnLongPress (首次 + 按 Interval 节流)
        └─ GetInteractState(Drag)     → 跳过累加（拖拽已开始）

OnPointerUp
  │
  ├─ StopCoroutine(LongPressRoutine)
  ├─ if (m_LongPressFired) → OnEndLongPress
  ├─ m_SuppressNextClick = true           ← 抑制紧随的 click
  └─ SetCurrentLongPress(null)
```

**节流规则**：`LongPressInterval <= 0` → `OnPress` **每帧**调用；`> 0` → 按秒间隔调用。默认 1 秒调用一次。

**取消时机**：拖拽开始（`OnBeginDrag` 中调用 `CancelLongPress()`）、指针移出（`OnPointerExit`）、松手（`OnPointerUp`）、组件 `OnDisable`、GameObject 销毁。

> 指针移出必须取消计时：小球件上"按下 → 移出 → 位移小于 `EventSystem.pixelDragThreshold`"不会触发 `OnBeginDrag`，
> 若不取消，长按会在指针已经离开的位置触发，并在屏幕任意位置松手时继续走 `OnEndLongPress`。

---

## 常见问题

**Q: 长按不触发，一直是拖拽？**
A: 检查 `EnableLongPress` 是否为 `true`，以及 `LongPressThresholdTime` 是否合理。拖拽只要像素移动超过 EventSystem 的 `pixelDragThreshold` 就会触发，此时长按会被取消（Drag/Press 互斥）。

**Q: 为什么 OnPointerClick 在长按后不触发？**
A: 刻意设计。长按结束后 `m_SuppressNextClick = true`，抑制紧随的 click 事件，避免长按操作被误判为点击。

**Q: 多个案例可能匹配同一个 Subject-Target 组合，如何控制优先级？**
A: 设置 `[InteractCase(..., order: N)]`，Order 越小的案例越先被匹配，**且严格决定优先级**：命中案例只会在同一 Order 组内前移（组内 LRU），不会越过 Order 更小的案例。首个返回 `true` 的案例命中后停止遍历。

**Q: 拖拽能不能按运行时数据决定？`EnableDrag` 是 prefab 上的静态开关。**
A: 重写 `protected override bool CanStartDrag(PointerEventData)`（返回 `false` 时本次手势完全不进入拖拽），或在 `OnStartDrag` 内调用 `CancelDrag()`。**不要**只在 `OnStartDrag` 里 `return` —— 框架无法感知"子类什么都没做"，该手势仍会被当成拖拽参与案例匹配。

**Q: 把子组件的 `EnableInteractive` 设为 `false`，为什么父节点的 `OnPointerDown` / `OnPointerClick` / `OnBeginDrag` 还是收不到？**
A: `EnableInteractive` 是本框架自己的开关，不参与 uGUI 的处理者筛选（`ExecuteEvents.ShouldSendToComponent` 只看 `isActiveAndEnabled`）。而 down / click / drag 走 `GetEventHandler` / `ExecuteHierarchy`，只会发给父链上**最深的**那个处理者。要让事件透传给父级，请对子组件用 **`enabled = false`**（或 `SetActive(false)`）：它会被 uGUI 跳过，父链继续向上找。enter / exit 不受影响 —— 它们本来就沿父链逐级派发。

**Q: 为什么 `CurrentFocusable` 有时是最外层（根）的那个组件？**
A: uGUI 的 enter 事件从射线命中的对象起、沿父链**由深到浅**逐级派发（`BaseInputModule.HandlePointerExitAndEnter`，默认 `m_SendPointerHoverToParent = true`），每级都会执行 `SetCurrentFocusable(this)`，所以最后写入的是最外层祖先。"父节点做落点/区域、子节点只做展示"的结构正好可以利用这一点。

**Q: 子节点滑出时，父节点的焦点会被一起清掉吗？**
A: 不会（本仓库已修复，未发版）。`OnPointerExit` 现在只在"全局槽位当前持有自己"时才清空。此前是无条件 `SetCurrentFocusable(null)`，会抹掉仍被悬停的祖先（uGUI 在父子间移动时不会给父节点补发 enter，焦点会一直空到重新进入）。

**Q: 多指触摸时 `CurrentFocusable` / `CurrentLongPress` 会不会被别的手指清掉？**
A: 会。三个全局槽位是**单指针语义**（框架按 `IInteractive` 实例记录，没有按 pointerId 分槽），而 uGUI 的 enter/exit 链是按每个指针各自维护的，所以 A 指移出/松开时可能清掉 B 指仍在使用的槽位。需要多指同时交互的场景请自行在组件内记录 pointerId，或多开 EventSystem。

**Q: `OnExecute` 里读到的拖拽源状态，和 `OnStopDrag` 里读到的为什么会打架？**
A: `OnExecute` 由 `UnityInteractive.Update` 驱动，`OnStopDrag` 由 `EventSystem.Update` 驱动，两者同帧但**先后顺序不确定**（两个都是默认执行顺序的 MonoBehaviour）。推荐两边都做幂等，例如"松手即回原位，除非已经被 OnExecute 消费"：`OnExecute` 只置消费标记，`OnStopDrag` 判断"未消费才归位"，谁先执行结果都一致。

**Q: 如何运行时动态切换案例的启用状态？**
A: 调用 `UnityInteractive.Instance.EnableInteractCase<T>()` / `DisableInteractCase<T>()`。

**Q: 为什么 OnStopPress 中 `CurrentPlacedItemType` 可能为 null？**
A: 长按过程中外部可能调用了 `RemoveItem()` 清空槽位。应在 `OnStopPress` 中做空检查。

**Q: `InteractiveComponent` 适用场景？**
A: 如果需要继承统一基类、利用单例状态追踪和案例系统 → 用 `InteractiveComponent`。

---

## 修复记录（本地未发布）

| # | 问题 | 修复 |
|---|------|------|
| 1 | `OnStartDrag` 无否决权，被否决的手势仍是 `CurrentDraggable` 并触发 `OnExecute` | 新增 `CanStartDrag()` / `CancelDrag()`；`OnUpdateDrag` / `OnStopDrag` 以"拖拽已被接受"为前提 |
| 2 | `IsValid` 只认已销毁对象，`SetActive(false)` / `enabled = false` 的组件永久占住全局槽位 | `IsValid` 追加 `isActiveAndEnabled` 判定；`OnDisable` 释放自己占用的三个槽位 |
| 3 | `OnPointerExit` / `OnPointerUp` 无条件清空全局槽位，会清掉别人的（含子节点清掉父节点的焦点） | 只在"当前槽位持有自己"时才清空 |
| 4 | `OnPointerExit` 不取消长按计时，长按会在指针已移开的位置触发 | `OnPointerExit` 调用 `CancelLongPress()`（并同步释放长按槽位、抑制紧随的 click） |
| 5 | `EnableInteractive = false` 无法透传事件给父级 | 文档写明：需要透传请用 `enabled = false`；补充 enter/exit 由深到浅冒泡的说明 |
| 6 | Case 构造失败抛异常会中断整个初始化（`if (interactCase == null) continue;` 为死代码） | 逐类型 `try/catch` + `LogError` 跳过；缺少 `public (Type,Type)` 构造函数时退化为无参激活 |
| 7 | 案例优先级随命中历史漂移（LRU 会越过 Order） | Order 严格优先，LRU 只在同一 Order 组内前移；`RegisterInteractCase` 按 Order 插入；Editor 显示真实匹配次序 |
| 附 | `OnExecute` 与 `OnEndDrag` 同帧顺序不确定未文档化 | `AbstractInteractCase` 注释 + 本 README FAQ 给出幂等写法 |

---

## 依赖

- `UnityEngine.InputSystem`（Input System Package）— 新输入系统设备检测
- `UnityEngine.EventSystems`（UGUI）— 事件数据和射线检测

---

## 许可

MIT License
