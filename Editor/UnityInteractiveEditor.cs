#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace NuoYan.Interactive
{
    [CustomEditor(typeof(UnityInteractive))]
    public class UnityInteractiveEditor : Editor
    {
        private UnityInteractive m_Interactive;
        private Vector2 m_ScrollPos;

        private static readonly GUIContent k_ThresholdLabel =
            new GUIContent("长按阈值(秒)", "超过该时长判定为长按，由 InteractiveComponent 读取");

        private void OnEnable()
        {
            m_Interactive = target as UnityInteractive;
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnEditorUpdate()
        {
            if (m_Interactive != null && EditorApplication.isPlaying)
            {
                Repaint();
            }
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.Space(10);

            DrawSettings();
            EditorGUILayout.Space(10);

            if (Application.isPlaying)
            {
                DrawRuntimeState();
                EditorGUILayout.Space(10);
            }

            DrawInteractCaseList();

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawSettings()
        {
            EditorGUILayout.LabelField("设置", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;

            var thresholdProp = serializedObject.FindProperty(nameof(UnityInteractive.LongPressThresholdTime));
            var intervalProp = serializedObject.FindProperty(nameof(UnityInteractive.LongPressInterval));
            if (thresholdProp != null)
            {
                EditorGUILayout.PropertyField(thresholdProp, k_ThresholdLabel);
            }
            else
            {
                // 兜底：直接改实例（非 play mode 也会标记 dirty）
                EditorGUI.BeginChangeCheck();
                float newThreshold = EditorGUILayout.FloatField(k_ThresholdLabel, m_Interactive.LongPressThresholdTime);
                if (EditorGUI.EndChangeCheck())
                {
                    m_Interactive.LongPressThresholdTime = newThreshold;
                    EditorUtility.SetDirty(target);
                }
            }

            if (intervalProp != null)
            {
                EditorGUILayout.PropertyField(intervalProp, new GUIContent("长按间隔(秒)", "小于等于 0 表示每帧调用"));
            }
            else
            {
                // 兜底：直接改实例（非 play mode 也会标记 dirty）
                EditorGUI.BeginChangeCheck();
                float newInterval = EditorGUILayout.FloatField("长按间隔(秒)", m_Interactive.LongPressInterval);
                if (EditorGUI.EndChangeCheck())
                {
                    m_Interactive.LongPressInterval = newInterval;
                    EditorUtility.SetDirty(target);
                }
            }

            EditorGUI.indentLevel--;
        }

        private void DrawRuntimeState()
        {
            EditorGUILayout.LabelField("运行时交互状态", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;

            DrawStateField("拖拽对象", m_Interactive.CurrentDraggable);
            DrawStateField("焦点对象", m_Interactive.CurrentFocusable);
            DrawStateField("长按对象", m_Interactive.CurrentLongPress);
            DrawStateField("活跃交互", m_Interactive.CurrentInteractCase);

            EditorGUI.indentLevel--;
        }

        private void DrawStateField(string label, object obj)
        {
            var rect = EditorGUILayout.GetControlRect();
            EditorGUI.LabelField(rect, label, obj == null ? "无" : obj.GetType().Name);
        }

        private void DrawInteractCaseList()
        {
            // 标题里的统计要在画标题之前算出来，所以先把两个来源都取出来
            var order = m_Interactive.ActiveInteractCaseOrder;
            int matchCount = order?.Count ?? 0;
            int registeredCount = m_Interactive.AllInteractCase?.Count ?? 0;

            int enabledCount = 0;
            for (var i = 0; i < matchCount; i++)
            {
                if (order[i].Enable) enabledCount++;
            }

            string countText = registeredCount == 0
                ? "未初始化"
                : registeredCount == matchCount
                    ? $"共 {matchCount} 个 · 启用 {enabledCount}"
                    : $"共 {matchCount} 个（已注册 {registeredCount}） · 启用 {enabledCount}";
            EditorGUILayout.LabelField($"交互情景列表（按实际匹配顺序）  {countText}", EditorStyles.boldLabel);

            if (registeredCount == 0)
            {
                EditorGUILayout.HelpBox(
                    Application.isPlaying
                        ? "没有已注册的交互情景（检查 [InteractCase] 特性、类型是否 abstract、以及是否有 public (Type, Type) 构造函数）"
                        : "交互情景尚未初始化：进入播放模式后由 UnityInteractive 扫描注册",
                    MessageType.Info);
                return;
            }

            if (matchCount == 0)
            {
                EditorGUILayout.HelpBox(
                    $"已注册 {registeredCount} 个案例，但活跃匹配列表为空（检查是否被外部直接修改了 AllInteractCase）",
                    MessageType.Warning);
                return;
            }

            if (enabledCount == 0)
            {
                EditorGUILayout.HelpBox("全部交互情景都被禁用（Enable = false），当前不会命中任何案例", MessageType.Warning);
            }

            // 显示运行时真实匹配顺序（链表快照），而不是 attribute 里的初始 Order：
            // 同 Order 组内会随命中历史前移，只看 Order 会误判。
            m_ScrollPos = EditorGUILayout.BeginScrollView(m_ScrollPos, GUILayout.MaxHeight(300));
            EditorGUI.indentLevel++;

            for (var i = 0; i < matchCount; i++)
            {
                var interactCase = order[i];
                bool isCurrent = interactCase == m_Interactive.CurrentInteractCase;
                DrawInteractCase(interactCase, i, isCurrent);
            }

            EditorGUI.indentLevel--;
            EditorGUILayout.EndScrollView();
        }

        private void DrawInteractCase(IInteractCase interactCase, int matchIndex, bool isCurrent)
        {
            var bgColor = GUI.backgroundColor;
            if (isCurrent) GUI.backgroundColor = Color.green;

            EditorGUILayout.BeginVertical("box");
            {
                EditorGUILayout.BeginHorizontal();
                {
                    // 启用开关
                    bool newEnable = EditorGUILayout.Toggle(interactCase.Enable, GUILayout.Width(16));
                    if (newEnable != interactCase.Enable)
                    {
                        interactCase.Enable = newEnable;
                        EditorUtility.SetDirty(target);
                    }

                    // 情景名
                    EditorGUILayout.LabelField(interactCase.GetType().Name, EditorStyles.boldLabel);

                    // 匹配次序 + 初始 Order
                    EditorGUILayout.LabelField($"#{matchIndex}  Order:{interactCase.Order}", GUILayout.Width(110));

                    if (isCurrent) EditorGUILayout.LabelField("活跃中", GUILayout.Width(45));
                }
                EditorGUILayout.EndHorizontal();

                // 第二行：Subject → Target
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField($"主体: {interactCase.Subject?.Name ?? "any"}  →  目标: {interactCase.Target?.Name ?? "any"}");
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndVertical();

            GUI.backgroundColor = bgColor;
        }
    }
}
#endif
