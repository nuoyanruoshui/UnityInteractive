using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace NuoYan.Interactive
{
    /// <summary>
    /// 基于 Unity 自身 EventSystem 的交互系统管理器。
    /// 维护全局当前拖拽/焦点/长按对象，每帧用它们匹配 IInteractCase
    /// （Order 升序严格优先，同 Order 内最近命中者靠前，首个命中即停）。
    /// 长按计时已下放到 InteractiveComponent 自身，本类仅跟踪 CurrentLongPress 引用。
    /// </summary>
    public sealed class UnityInteractive : MonoBehaviour
    {
        private static UnityInteractive m_Instance;
        private bool m_IsInitialized;
        public static UnityInteractive Instance
        {
            get
            {
                if (m_Instance == null)
                {
                    m_Instance = FindFirstObjectByType<UnityInteractive>();
                    if (m_Instance == null)
                    {
                        GameObject go = new GameObject("[UnityInteractive]");
                        m_Instance = go.AddComponent<UnityInteractive>();
                        DontDestroyOnLoad(go);
                    }
                    m_Instance.InitOnce();
                }
                return m_Instance;
            }
        }

        /// <summary>
        /// 不创建实例的访问入口。用于组件失活/销毁等清理路径，
        /// 避免在场景卸载或退出播放时被 <see cref="Instance"/> 反向创建出管理器。
        /// </summary>
        public static UnityInteractive InstanceOrNull => m_Instance;

        public Dictionary<Type, IInteractCase> AllInteractCase { get; private set; }
        private LinkedList<IInteractCase> m_ActiveInteractCases;
        private readonly List<IInteractCase> m_ActiveCaseOrderSnapshot = new List<IInteractCase>();
        public IInteractCase CurrentInteractCase { get; private set; }
        public IDraggable CurrentDraggable { get; private set; }
        public IFocusable CurrentFocusable { get; private set; }
        public ILongPressHandler CurrentLongPress { get; private set; }

        /// <summary>
        /// 当前匹配顺序快照（下标 0 最先参与匹配），仅供调试/Editor 显示。
        /// 按 Order 升序，同 Order 内最近命中者靠前。
        /// </summary>
        public IReadOnlyList<IInteractCase> ActiveInteractCaseOrder
        {
            get
            {
                m_ActiveCaseOrderSnapshot.Clear();
                for (var node = m_ActiveInteractCases?.First; node != null; node = node.Next)
                    m_ActiveCaseOrderSnapshot.Add(node.Value);
                return m_ActiveCaseOrderSnapshot;
            }
        }

        [Tooltip("进入长按的阈值时间（秒），由 InteractiveComponent 读取")]
        public float LongPressThresholdTime = 1.5f;
        /// <summary>长按触发后 OnPress 的调用间隔（秒）。<=0 表示每帧调用。</summary>
        [Tooltip("长按触发后 OnPress 的调用间隔（秒）。<=0 表示每帧调用。")]
        public float LongPressInterval = 1f;

        private void InitOnce()
        {
            if (m_IsInitialized) return;
            m_IsInitialized = true;
            AllInteractCase = new Dictionary<Type, IInteractCase>();
            m_ActiveInteractCases = new LinkedList<IInteractCase>();

            // 收集后按 Order 排序，保证初始匹配优先级确定性
            var collected = new List<(IInteractCase Case, int Order)>();
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException)
                {
                    // 跳过加载失败的程序集，避免一个坏程序集拖垮整个初始化
                    continue;
                }

                foreach (var type in types)
                {
                    if (type.IsAbstract || !typeof(IInteractCase).IsAssignableFrom(type)) continue;

                    InteractCaseAttribute attribute = type.GetCustomAttribute<InteractCaseAttribute>();
                    if (attribute == null) continue;

                    // 单个案例写错不应该拖垮整个交互系统：逐类型兜异常，失败只跳过该案例
                    if (!TryCreateInteractCase(type, attribute, out var interactCase, out var error))
                    {
                        Debug.LogError($"[UnityInteractive] 交互案例 {type.FullName} 创建失败，已跳过：{error}");
                        continue;
                    }

                    AllInteractCase.Add(type, interactCase);
                    interactCase.Enable = attribute.EnableExecuteOnLoad;
                    interactCase.Order = attribute.Order;
                    collected.Add((interactCase, attribute.Order));
                }
            }

            foreach (var item in collected.OrderBy(x => x.Order))
            {
                m_ActiveInteractCases.AddLast(item.Case);
            }
        }

        /// <summary>
        /// 反射创建交互案例实例。
        /// <para>
        /// 首选 public <c>(Type subject, Type target)</c> 构造函数；没有时退化为 public 无参构造
        /// （泛型基类的构造函数都是 protected，而构造函数不会被继承，派生类很容易漏写）。
        /// 失败时返回 false 并给出原因，由调用方 LogError 后跳过该案例，不影响其它案例。
        /// </para>
        /// </summary>
        private static bool TryCreateInteractCase(Type type, InteractCaseAttribute attribute, out IInteractCase interactCase, out string error)
        {
            interactCase = null;
            error = null;

            try
            {
                interactCase = Activator.CreateInstance(type, attribute.InteractSubject, attribute.InteractTarget) as IInteractCase;
                if (interactCase != null) return true;
                error = $"类型未实现 {nameof(IInteractCase)}";
                return false;
            }
            catch (MissingMethodException)
            {
                // 落到下面的退化路径
            }
            catch (Exception e)
            {
                error = $"({nameof(Type)}, {nameof(Type)}) 构造函数抛出异常：{e}";
                return false;
            }

            try
            {
                interactCase = Activator.CreateInstance(type) as IInteractCase;
            }
            catch (Exception e)
            {
                error = $"既没有 public ({nameof(Type)}, {nameof(Type)}) 构造函数，也没有可用的 public 无参构造函数：{e}";
                return false;
            }

            if (interactCase == null)
            {
                error = $"类型未实现 {nameof(IInteractCase)}";
                return false;
            }

            // 无参构造不会带上特性里的 Subject/Target，这里补上未赋值的那部分
            if (interactCase is AbstractInteractCase abstractCase)
            {
                if (abstractCase.Subject == null) abstractCase.Subject = attribute.InteractSubject;
                if (abstractCase.Target == null) abstractCase.Target = attribute.InteractTarget;
            }
            return true;
        }

        void Update()
        {
            // 清除悬挂引用：已销毁，或已 SetActive(false) / enabled=false
            if (!IsValid(CurrentDraggable)) SetCurrentDraggable(null);
            if (!IsValid(CurrentFocusable)) SetCurrentFocusable(null);
            if (!IsValid(CurrentLongPress)) SetCurrentLongPress(null);

            // 执行交互案例（命中首个即停止遍历）
            IInteractCase activeCase = null;
            var context = new InteractContext(CurrentFocusable, CurrentDraggable, CurrentLongPress);
            foreach (var item in m_ActiveInteractCases)
            {
                if (item.Enable && item.Execute(context))
                {
                    activeCase = item;
                    break;
                }
            }

            // LRU：把最近活跃案例前移，但只在同一 Order 组内前移，
            // 保证 InteractCaseAttribute.Order 始终严格决定优先级（不随运行历史漂移）。
            if (activeCase != null)
            {
                CurrentInteractCase = activeCase;
                PromoteActiveCase(activeCase);
            }

            // 交互案例处理完后清理拖拽状态
            if (CurrentDraggable != null && EasyInput.PointerUp())
            {
                SetCurrentDraggable(null);
            }
        }

        /// <summary>
        /// 命中后把案例前移：Order 严格优先，仅在本 Order 组内前移（同 Order 内最近命中者靠前）。
        /// </summary>
        private void PromoteActiveCase(IInteractCase interactCase)
        {
            var first = m_ActiveInteractCases.First;
            while (first != null && first.Value.Order < interactCase.Order) first = first.Next;
            // first == null 不可能（interactCase 一定在链表里）；已是本组首位则无需移动
            if (first == null || ReferenceEquals(first.Value, interactCase)) return;

            m_ActiveInteractCases.Remove(interactCase);
            m_ActiveInteractCases.AddBefore(first, interactCase);
        }

        /// <summary>
        /// 按 Order 升序插入到链表中（同 Order 追加在本组末尾，保持注册顺序稳定）。
        /// </summary>
        private void InsertByOrder(IInteractCase interactCase)
        {
            var node = m_ActiveInteractCases.First;
            while (node != null && node.Value.Order <= interactCase.Order) node = node.Next;
            if (node == null) m_ActiveInteractCases.AddLast(interactCase);
            else m_ActiveInteractCases.AddBefore(node, interactCase);
        }

        /// <summary>
        /// 引用是否仍然可用：已销毁、或已失活（<c>SetActive(false)</c> / <c>enabled = false</c>）都算不可用。
        /// <para>
        /// 只判断"是不是 null"是不够的：Unity 的假 null 只能识别已销毁对象，
        /// 被 SetActive(false) 的组件既不会收到 OnPointerExit（uGUI 会跳过非激活对象），
        /// 又会被误判为有效，于是永久占住全局槽位。
        /// </para>
        /// </summary>
        private static bool IsValid(object obj)
        {
            if (obj == null) return true;                // 真 null：槽位本来就是空的
            var behaviour = obj as MonoBehaviour;
            if ((object)behaviour == null) return true;  // 非 MonoBehaviour 实现：没有销毁/停用语义
            if (behaviour == null) return false;         // Unity 假 null：已销毁
            return behaviour.isActiveAndEnabled;         // 已失活：同样视为无效
        }

        /// <summary>
        /// 运行时注册交互案例
        /// </summary>
        public void RegisterInteractCase(IInteractCase interactCase, bool enable = true)
        {
            var type = interactCase.GetType();
            if (AllInteractCase.ContainsKey(type)) return;
            AllInteractCase.Add(type, interactCase);
            interactCase.Enable = enable;
            InsertByOrder(interactCase);
        }

        /// <summary>
        /// 运行时注销交互案例
        /// </summary>
        public void UnregisterInteractCase<T>() where T : IInteractCase
        {
            var type = typeof(T);
            if (AllInteractCase.TryGetValue(type, out var case_))
            {
                m_ActiveInteractCases.Remove(case_);
                AllInteractCase.Remove(type);
            }
        }

        /// <summary>
        /// 运行时禁用交互案例
        /// </summary>
        public void DisableInteractCase<T>() where T : IInteractCase
        {
            if (AllInteractCase.TryGetValue(typeof(T), out var case_))
                case_.Enable = false;
        }

        /// <summary>
        /// 运行时启用交互案例
        /// </summary>
        public void EnableInteractCase<T>() where T : IInteractCase
        {
            if (AllInteractCase.TryGetValue(typeof(T), out var case_))
                case_.Enable = true;
        }

        /// <summary>
        /// 设置当前拖拽对象
        /// </summary>
        public void SetCurrentDraggable(IDraggable dragable) => CurrentDraggable = dragable;

        /// <summary>
        /// 设置当前焦点对象
        /// </summary>
        public void SetCurrentFocusable(IFocusable focusable) => CurrentFocusable = focusable;

        /// <summary>
        /// 设置当前长按对象
        /// </summary>
        public void SetCurrentLongPress(ILongPressHandler focusable) => CurrentLongPress = focusable;

        private void OnDestroy()
        {
            CurrentDraggable = null;
            CurrentFocusable = null;
            CurrentLongPress = null;
            CurrentInteractCase = null;
            AllInteractCase?.Clear();
            m_ActiveInteractCases?.Clear();
            m_Instance = null;
        }
    }
}
