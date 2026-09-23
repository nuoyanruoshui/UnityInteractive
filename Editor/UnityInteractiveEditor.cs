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
            EditorGUILayout.LabelField("交互情景列表（按实际匹配顺序）", EditorStyles.boldLabel);

            if (m_Interactive.AllInteractCase == null || m_Interactive.AllInteractCase.Count == 0)
            {
                EditorGUILayout.HelpBox("没有已注册的交互情景", MessageType.Info);
                return;
            }

            // 显示运行时真实匹配顺序（链表快照），而不是 attribute 里的初始 Order：
            // 同 Order 组内会随命中历史前移，只看 Order 会误判。
            var order = m_Interactive.ActiveInteractCaseOrder;
            if (order == null || order.Count == 0)
            {
                EditorGUILayout.HelpBox("交互情景尚未初始化（非播放模式首次访问才会扫描注册）", MessageType.Info);
                return;
            }

            m_ScrollPos = EditorGUILayout.BeginScrollView(m_ScrollPos, GUILayout.MaxHeight(300));
            EditorGUI.indentLevel++;

            for (var i = 0; i < order.Count; i++)
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
