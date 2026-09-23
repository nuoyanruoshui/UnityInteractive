using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace NuoYan.Interactive
{
    /// <summary>
    /// 交互情景标识
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class InteractCaseAttribute : Attribute
    {
        public Type InteractSubject;
        public Type InteractTarget;
        public bool EnableExecuteOnLoad;
        /// <summary>
        /// 匹配优先级，越小越先，且<strong>严格决定</strong>优先级（运行时不会被 LRU 越过）。
        /// 同 Order 时按发现顺序排列，其中被命中过的案例会在本 Order 组内前移（组内 LRU）。
        /// </summary>
        public int Order;

        public InteractCaseAttribute(Type subject, Type target, bool enableExecuteOnLoad = true, int order = 0)
        {
            InteractSubject = subject;
            InteractTarget = target;
            this.EnableExecuteOnLoad = enableExecuteOnLoad;
            Order = order;
        }
    }

    /// <summary>
    /// 交互情景,同时处理拖拽又可长按
    /// <para>
    /// 注意驱动来源不同：<see cref="Execute"/> 由 <c>UnityInteractive.Update</c> 驱动，
    /// 而组件的 <c>OnEndDrag</c> 由 <c>EventSystem.Update</c> 驱动，两者同帧但<strong>先后顺序不确定</strong>
    /// （都是默认执行顺序的 MonoBehaviour）。因此在 <see cref="OnDragExecute"/> 里读到的
    /// "拖拽预览 / 源对象状态"可能已经被 <c>OnEndDrag</c> 改过。
    /// </para>
    /// <para>
    /// 推荐写法：两边都做幂等 —— 例如"松手即回原位，除非已经被 OnExecute 消费"：
    /// <c>OnDragExecute</c> 只做落点判定与消费标记；<c>OnStopDrag</c> 里判断"未被消费才回原位"，
    /// 这样谁先执行结果都一致。
    /// </para>
    /// </summary>
    public abstract class AbstractInteractCase : IInteractCase
    {
        public Type Subject { get; set; }
        public Type Target { get; set; }
        public bool Enable { get; set; }
        public int Order { get; set; }
        private bool m_IsEnter = false;
        private bool m_IsExit = true;
        private int m_LastSubjectInstanceId = 0;
        private int m_LastTargetInstanceId = 0;
        public AbstractInteractCase(Type subject, Type target)
        {
            Subject = subject;
            Target = target;
        }

        public virtual bool Execute(InteractContext context)
        {
            if (context.Focusable == null)
            {
                ExitIfNeeded();
                return false;
            }

            // 匹配：Drag 路径与 LongPress 路径任一路径满足即进入
            bool targetOk = Target.IsAssignableFrom(context.Focusable.InteractableType);
            bool dragOk = targetOk && context.Draggable != null
                && Subject.IsAssignableFrom(context.Draggable.InteractableType);
            bool pressOk = targetOk && context.LongPressHandler != null
                && Subject.IsAssignableFrom(context.LongPressHandler.InteractableType);

            if (!dragOk && !pressOk)
            {
                ExitIfNeeded();
                return false;
            }

            int currentSubjectId = dragOk
                ? (context.Draggable as MonoBehaviour)?.GetInstanceID() ?? 0
                : (context.LongPressHandler as MonoBehaviour)?.GetInstanceID() ?? 0;
            int currentTargetId = (context.Focusable as MonoBehaviour)?.GetInstanceID() ?? 0;

            if (m_IsEnter && (currentSubjectId != m_LastSubjectInstanceId || currentTargetId != m_LastTargetInstanceId))
                ExitIfNeeded();

            if (m_IsExit)
            {
                m_IsExit = false;
                if (dragOk) OnDragEnter(context.Draggable, context.Focusable);
                if (pressOk) OnLongPressEnter(context.LongPressHandler, context.Focusable);
                m_IsEnter = true;
                m_LastSubjectInstanceId = currentSubjectId;
                m_LastTargetInstanceId = currentTargetId;
            }

            if (dragOk) OnDragExecute(context.Draggable, context.Focusable);
            if (pressOk) OnLongPressExecute(context.LongPressHandler, context.Focusable);
            return true;
        }

        private void ExitIfNeeded()
        {
            if (m_IsEnter)
            {
                m_IsEnter = false;
                OnExit();
                m_IsExit = true;
                m_LastSubjectInstanceId = 0;
                m_LastTargetInstanceId = 0;
            }
        }
        protected virtual void OnDragExecute(IDraggable subject, IFocusable target)
        {

        }
        protected virtual void OnLongPressExecute(ILongPressHandler subject, IFocusable target)
        {

        }
        protected virtual void OnDragEnter(IDraggable subject, IFocusable target)
        {
        }
        protected virtual void OnLongPressEnter(ILongPressHandler subject, IFocusable target)
        {
        }

        protected virtual void OnExit()
        {
        }


        protected virtual bool Stop => EasyInput.PointerUp();
    }

    /// <summary>
    /// 拖拽主体 x 焦点目标的交互情景（向后兼容 AbstractInteractCase）。
    /// 重写此类即仅处理 Drag 路径，OnExecute/OnEnter 会被桥接到 OnDragExecute/OnDragEnter。
    /// </summary>
    public abstract class DragSubjectFocusTargetInteractCase : AbstractInteractCase
    {
        public DragSubjectFocusTargetInteractCase(Type subject, Type target) : base(subject, target) { }

        protected abstract void OnExecute(IDraggable subject, IFocusable target);
        protected virtual void OnEnter(IDraggable subject, IFocusable target) { }

        protected override void OnDragExecute(IDraggable subject, IFocusable target)
            => OnExecute(subject, target);
        protected override void OnDragEnter(IDraggable subject, IFocusable target)
            => OnEnter(subject, target);
    }
    /// <summary>
    /// 长按主体 x 焦点目标的交互情景（向后兼容 AbstractInteractCase）。
    /// </summary> <summary>
    public abstract class LongPressSubjectFocusTargetInteractCase : AbstractInteractCase
    {
        public LongPressSubjectFocusTargetInteractCase(Type subject, Type target) : base(subject, target) { }

        protected abstract void OnExecute(ILongPressHandler subject, IFocusable target);
        protected virtual void OnEnter(ILongPressHandler subject, IFocusable target) { }

        protected override void OnLongPressExecute(ILongPressHandler subject, IFocusable target) => OnExecute(subject, target);
        protected override void OnLongPressEnter(ILongPressHandler subject, IFocusable target) => OnEnter(subject, target);
    }

    /// <summary>
    /// 泛型版本：免去 OnExecute / OnEnter 内的手动 as 类型转换。
    /// Subject/Target 由泛型参数推断，构造时仍兼容 (Type,Type) 以便反射激活。
    /// <para>
    /// 注意：本类的两个构造函数都是 protected，而构造函数不会被继承 ——
    /// 派生类需要自己声明 <c>public XxxCase(Type subject, Type target) : base(subject, target) { }</c>；
    /// 若只写了无参构造，框架会退化为无参激活，并用 <see cref="InteractCaseAttribute"/> 里的
    /// Subject/Target 补齐（泛型基类的 Subject/Target 已由基类构造函数填好）。
    /// </para>
    /// </summary>
    public abstract class DragSubjectFocusTargetInteractCase<TSubject, TTarget> : DragSubjectFocusTargetInteractCase
        where TSubject : class, IDraggable
        where TTarget : class, IFocusable
    {
        protected DragSubjectFocusTargetInteractCase() : base(typeof(TSubject), typeof(TTarget)) { }
        protected DragSubjectFocusTargetInteractCase(Type subject, Type target) : base(subject, target) { }

        protected abstract void OnExecute(TSubject subject, TTarget target);
        protected virtual void OnEnter(TSubject subject, TTarget target) { }

        protected override void OnExecute(IDraggable subject, IFocusable target)
            => OnExecute(subject as TSubject, target as TTarget);
        protected override void OnEnter(IDraggable subject, IFocusable target)
            => OnEnter(subject as TSubject, target as TTarget);
    }
    /// <summary>
    /// 泛型版本：免去 OnExecute / OnEnter 内的手动 as 类型转换。
    /// </summary>
    /// <typeparam name="TSubject"></typeparam>
    /// <typeparam name="TTarget"></typeparam>
    public abstract class LongPressSubjectFocusTargetInteractCase<TSubject, TTarget> : LongPressSubjectFocusTargetInteractCase
        where TSubject : class, ILongPressHandler
        where TTarget : class, IFocusable
    {
        protected LongPressSubjectFocusTargetInteractCase() : base(typeof(TSubject), typeof(TTarget)) { }
        protected LongPressSubjectFocusTargetInteractCase(Type subject, Type target) : base(subject, target) { }

        protected abstract void OnExecute(TSubject subject, TTarget target);
        protected virtual void OnEnter(TSubject subject, TTarget target) { }

        protected override void OnExecute(ILongPressHandler subject, IFocusable target)
            => OnExecute(subject as TSubject, target as TTarget);

    }
}
