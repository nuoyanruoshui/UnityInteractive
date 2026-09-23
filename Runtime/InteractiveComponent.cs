using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace NuoYan.Interactive
{
    /// <summary>
    /// 交互组件基类。基于 Unity EventSystem 接收事件，转成受保护的虚方法供子类重写。
    /// 拖拽(Drag) / 长按(Press) 互斥。
    /// <para>
    /// 长按取消时机：<see cref="OnBeginDrag"/>（拖拽开始）、<see cref="OnPointerExit"/>（指针移出）、
    /// <see cref="OnPointerUp"/>（松手）、<see cref="OnDisable"/>（失活/销毁）。
    /// </para>
    /// <para>
    /// 运行时否决拖拽：重写 <see cref="CanStartDrag"/>，或在 <see cref="OnStartDrag"/> 内调用
    /// <see cref="CancelDrag"/>。被否决的手势不会成为
    /// <see cref="UnityInteractive.CurrentDraggable"/>，也不会收到 OnUpdateDrag / OnStopDrag。
    /// </para>
    /// </summary>
    public class InteractiveComponent : MonoBehaviour, IDraggable, IFocusable, ISelectable, ILongPressHandler
    {
        public const string Drag = "Drag";//拖拽
        public const string Focus = "Focus";//焦点
        public const string Press = "Press";//长按

        [SerializeField] private bool m_EnableInteractive = true;
        [SerializeField] private bool m_EnableDrag = true;
        [SerializeField] private bool m_EnableLongPress = true;
        public Type InteractableType => this.GetType();
        /// <summary>
        /// 是否启用自身的交互逻
        /// </summary>
        /// <value></value>
        public virtual bool EnableInteractive { get => m_EnableInteractive; set => m_EnableInteractive = value; }
        public virtual bool EnableDrag { get => m_EnableDrag; set => m_EnableDrag = value; }
        public virtual bool EnableLongPress { get => m_EnableLongPress; set => m_EnableLongPress = value; }


        // 交互事件全部在主线程派发，无需并发容器
        private readonly Dictionary<string, bool> m_InteractState = new Dictionary<string, bool>();

        // 长按计时（自包含，不依赖 UnityInteractive 的 Update）
        private bool m_Pressing;
        private float m_PressTime;
        private bool m_LongPressFired;
        private float m_NextPressTime; // 下次 OnPress 触发时间（按 LongPressInterval 节流）
        private bool m_SuppressNextClick; // 长按结束后抑制紧随的 click
        private Coroutine m_PressCoroutine; // 仅按下时运行，无按下时零 Update 开销
        private PointerEventData m_PressEventData;

        // 拖拽会话：只有被接受的手势才置 m_IsDragging，OnUpdateDrag / OnStopDrag 都以它为前提
        private bool m_IsDragging;
        private bool m_DragRejected; // 本次 OnBeginDrag 内被 CanStartDrag / CancelDrag 否决

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (EnableInteractive && EnableDrag && !GetInteractState(Press) && !m_IsDragging)
            {
                CancelLongPress(); // 按下未到阈值即开始拖拽，取消长按计时
                if (!CanStartDrag(eventData)) return; // 运行时否决：不进入 Drag 状态、不注册、不回调

                m_DragRejected = false;
                SetState(Drag, true);
                // 先注册再回调：保持“OnStartDrag 内可读 CurrentDraggable”的既有行为，
                // 若回调内调用 CancelDrag() 则在下面立即撤销注册（匹配只在 Update 中发生，不会漏派发）
                UnityInteractive.Instance.SetCurrentDraggable(this);
                OnStartDrag(eventData);

                if (m_DragRejected || !GetInteractState(Drag))
                {
                    // 子类否决了本次拖拽：撤销刚注册的槽位与状态，整个手势期间不再回调
                    m_DragRejected = false;
                    SetState(Drag, false);
                    ClearCurrentDraggableIfSelf();
                    return;
                }

                m_IsDragging = true;
            }
        }

        public void OnDrag(PointerEventData eventData)
        {
            // m_IsDragging 才能保证：被否决 / 未真正开始的拖拽不会驱动业务通路
            if (EnableInteractive && EnableDrag && m_IsDragging)
            {
                OnUpdateDrag(eventData);
            }
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (!EnableInteractive || !EnableDrag || !m_IsDragging) return;

            m_IsDragging = false;
            SetState(Drag, false);
            OnStopDrag(eventData);
            // 全局 CurrentDraggable 不在这里清：松手帧 UnityInteractive.Update 仍需用它匹配案例，
            // 清理由 Update 末尾的 EasyInput.PointerUp() 分支负责。
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            // 长按刚结束，抑制随之而来的 click，避免与长按重复触发
            if (m_SuppressNextClick)
            {
                m_SuppressNextClick = false;
                return;
            }
            if (EnableInteractive)
            {
                OnSelect(eventData);
            }
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (EnableInteractive)
            {
                // enter 沿 Transform 父链由深到浅逐级派发（uGUI 默认 m_SendPointerHoverToParent = true），
                // 每级都会写一次，因此最终持有焦点的是最外层的那个组件。
                UnityInteractive.Instance.SetCurrentFocusable(this);
                SetState(Focus, true);
                OnFocus(eventData);
            }
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (!EnableInteractive) return;

            OnLostFocus(eventData);
            SetState(Focus, false);

            // 指针移出即终止长按计时。否则位移未达 EventSystem.pixelDragThreshold 时不会触发
            // OnBeginDrag，长按会在指针已经离开的位置触发，并在屏幕任意位置松手时继续走 OnEndLongPress。
            if (CancelLongPress()) m_SuppressNextClick = true; // 与 OnPointerUp 一致：长按结束后抑制紧随的 click

            // 只清自己占用的槽位：uGUI 会把 Exit 沿父链派发给每一级，
            // 无条件清空会抹掉仍被悬停的祖先（或另一个指针）写下的焦点。
            ClearCurrentFocusableIfSelf();
            ClearCurrentLongPressIfSelf();
        }

        /// <summary>
        /// 拖拽前置判定：返回 false 时本次手势不进入拖拽（不写 Drag 状态、不注册为
        /// <see cref="UnityInteractive.CurrentDraggable"/>、不会收到 OnStartDrag/OnUpdateDrag/OnStopDrag）。
        /// <para>需要在运行时按数据决定"能不能拖"时重写本方法，不要用 OnStartDrag 里直接 return —— 那样框架无法感知否决。</para>
        /// </summary>
        protected virtual bool CanStartDrag(PointerEventData eventData) => true;

        /// <summary>
        /// 否决本次拖拽。可在 <see cref="OnStartDrag"/> 内调用；拖拽已经开始时调用会立即中止
        /// （清 Drag 状态并释放全局槽位，但不会再回调 <see cref="OnStopDrag"/>，请自行收尾表现）。
        /// </summary>
        protected void CancelDrag()
        {
            m_DragRejected = true;
            if (!m_IsDragging) return; // OnBeginDrag 的收尾逻辑会统一处理

            m_IsDragging = false;
            SetState(Drag, false);
            ClearCurrentDraggableIfSelf();
        }

        /// <summary>仅当全局槽位当前持有自己时才清空，避免抹掉别人的注册。</summary>
        private void ClearCurrentDraggableIfSelf()
        {
            var manager = UnityInteractive.InstanceOrNull;
            if (manager != null && ReferenceEquals(manager.CurrentDraggable, this))
                manager.SetCurrentDraggable(null);
        }

        private void ClearCurrentFocusableIfSelf()
        {
            var manager = UnityInteractive.InstanceOrNull;
            if (manager != null && ReferenceEquals(manager.CurrentFocusable, this))
                manager.SetCurrentFocusable(null);
        }

        private void ClearCurrentLongPressIfSelf()
        {
            var manager = UnityInteractive.InstanceOrNull;
            if (manager != null && ReferenceEquals(manager.CurrentLongPress, this))
                manager.SetCurrentLongPress(null);
        }

        protected virtual void OnSelect(PointerEventData eventData) { }
        protected virtual void OnStartDrag(PointerEventData eventData) { }
        protected virtual void OnUpdateDrag(PointerEventData eventData) { }
        protected virtual void OnStopDrag(PointerEventData eventData) { }
        protected virtual void OnFocus(PointerEventData eventData) { }
        protected virtual void OnLostFocus(PointerEventData eventData) { }
        protected virtual void OnStartPress(PointerEventData eventData) { }
        protected virtual void OnPress(PointerEventData eventData) { }
        protected virtual void OnStopPress(PointerEventData eventData) { }

        /// <summary>
        /// 获取交互状态。所有 key 都为 true 才返回 true。Drag 与 Press 互斥。
        /// </summary>
        /// <param name="keys">"Drag","Focus","Press"</param>
        public bool GetInteractState(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (!m_InteractState.TryGetValue(key, out var value) || !value)
                    return false;
            }
            return true;
        }

        private void SetState(string key, bool value)
        {
            m_InteractState[key] = value;
        }

        #region 长按（自包含计时）

        public void OnBeginLongPress(PointerEventData eventData)
        {
            if (EnableInteractive && EnableLongPress && !GetInteractState(Drag))
            {
                SetState(Press, true);
                m_LongPressFired = true;
                OnStartPress(eventData);
            }
        }

        public void OnLongPress(PointerEventData eventData)
        {
            if (EnableInteractive && EnableLongPress && !GetInteractState(Drag))
            {
                OnPress(eventData);
            }
        }

        public void OnEndLongPress(PointerEventData eventData)
        {
            if (EnableInteractive && EnableLongPress && !GetInteractState(Drag))
            {
                OnStopPress(eventData);
                SetState(Press, false);
            }
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (!EnableInteractive) return;
            m_Pressing = true;
            m_PressTime = 0f;
            m_LongPressFired = false;
            m_SuppressNextClick = false;
            m_PressEventData = eventData;
            UnityInteractive.Instance.SetCurrentLongPress(this);
            if (m_PressCoroutine == null)
            {
                m_PressCoroutine = StartCoroutine(LongPressRoutine());
            }
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (!m_Pressing) return;
            StopLongPressRoutine();
            m_PressEventData = eventData;
            if (m_LongPressFired)
            {
                OnEndLongPress(m_PressEventData);
                m_SuppressNextClick = true; // 抑制紧随的 click
            }
            m_Pressing = false;
            m_LongPressFired = false;
            m_PressTime = 0f;
            ClearCurrentLongPressIfSelf();
        }

        /// <summary>
        /// 长按计时协程：仅在被按下期间运行，无按下时本组件零 Update 开销。
        /// 用协程替代 Update，避免大量 InteractiveComponent 实例每帧空跑。
        /// </summary>
        private IEnumerator LongPressRoutine()
        {
            while (m_Pressing)
            {
                yield return null;
                if (!m_Pressing) break;
                if (GetInteractState(Drag)) continue; // 拖拽中不累加（Drag 与 Press 互斥）
                m_PressTime += Time.deltaTime;
                if (!m_LongPressFired && m_PressTime >= UnityInteractive.Instance.LongPressThresholdTime)
                {
                    OnBeginLongPress(m_PressEventData);
                    // 仅 guard 通过（m_LongPressFired 已置 true）才首次 OnPress + 节流
                    if (m_LongPressFired)
                    {
                        OnLongPress(m_PressEventData);
                        m_NextPressTime = m_PressTime + UnityInteractive.Instance.LongPressInterval;
                    }
                }
                else if (m_LongPressFired)
                {
                    // LongPressInterval <= 0 → 每帧调用；否则按间隔节流
                    if (UnityInteractive.Instance.LongPressInterval <= 0f || m_PressTime >= m_NextPressTime)
                    {
                        OnLongPress(m_PressEventData);
                        if (UnityInteractive.Instance.LongPressInterval > 0f)
                            m_NextPressTime = m_PressTime + UnityInteractive.Instance.LongPressInterval;
                    }
                }
            }
            m_PressCoroutine = null;
        }

        private void StopLongPressRoutine()
        {
            if (m_PressCoroutine != null)
            {
                StopCoroutine(m_PressCoroutine);
                m_PressCoroutine = null;
            }
        }

        /// <summary>
        /// 取消长按计时。返回是否取消了一次"已经触发过"的长按
        /// （true 表示紧接着的 click 应当被抑制）。
        /// </summary>
        private bool CancelLongPress()
        {
            StopLongPressRoutine();
            bool wasFired = m_LongPressFired;
            if (wasFired)
            {
                OnEndLongPress(m_PressEventData);
            }
            m_Pressing = false;
            m_LongPressFired = false;
            m_PressTime = 0f;
            return wasFired;
        }

        private void OnDisable()
        {
            // 组件禁用/销毁时清理自身状态，避免悬挂回调
            StopLongPressRoutine();
            if (m_Pressing && m_LongPressFired)
            {
                OnEndLongPress(m_PressEventData);
            }
            m_Pressing = false;
            m_LongPressFired = false;
            m_PressTime = 0f;
            m_SuppressNextClick = false;
            m_IsDragging = false;
            m_DragRejected = false;
            m_InteractState[Drag] = false;
            m_InteractState[Press] = false;
            m_InteractState[Focus] = false;
            // 自己占用的三个全局槽位在这里就地释放：
            // 失活的组件收不到 OnPointerExit（uGUI ExecuteEvents.GetEventList 会跳过
            // !activeInHierarchy 的对象），只靠 Update 的 IsValid 兜底会残留到下一帧，
            // 且 SetActive(false) / enabled=false 并不会让 IsValid 判定失效。
            ClearCurrentDraggableIfSelf();
            ClearCurrentFocusableIfSelf();
            ClearCurrentLongPressIfSelf();
        }
        #endregion
    }
}
