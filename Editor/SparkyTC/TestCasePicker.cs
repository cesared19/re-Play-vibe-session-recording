using System;
using UnityEditor;
using UnityEngine;

namespace XRSessionCapture.Editor.SparkyTC
{
    /// <summary>A searchable list of the SparkyTC project's test cases, to link a capture to one.</summary>
    public class TestCasePicker : PopupWindowContent
    {
        readonly Action<SparkyTCClient.Project, SparkyTCClient.TestCase> m_OnPick;
        SparkyTCClient.Project m_Project;
        string m_Query = string.Empty;
        string m_SearchedQuery;
        SparkyTCClient.TestCase[] m_Results = Array.Empty<SparkyTCClient.TestCase>();
        string m_Error;
        bool m_Loading;
        Vector2 m_Scroll;
        int m_Request;

        public TestCasePicker(Action<SparkyTCClient.Project, SparkyTCClient.TestCase> onPick)
        {
            m_OnPick = onPick;
        }

        public override Vector2 GetWindowSize() => new Vector2(420, 360);

        public override void OnOpen() => Search();

        async void Search()
        {
            var request = ++m_Request;
            m_SearchedQuery = m_Query;
            m_Loading = true;
            m_Error = null;
            try
            {
                var client = SparkyTCClient.FromSettings();
                // the project's code makes the links readable (PRJ-12)
                if (m_Project == null)
                    m_Project = await client.GetProjectAsync();
                var results = await client.SearchTestCasesAsync(m_Query);
                if (request != m_Request)
                    return;
                m_Results = results;
            }
            catch (SparkyTCException e)
            {
                if (request == m_Request)
                    m_Error = e.Message;
            }
            finally
            {
                if (request == m_Request)
                    m_Loading = false;
                editorWindow?.Repaint();
            }
        }

        public override void OnGUI(Rect rect)
        {
            EditorGUILayout.LabelField("Link to a SparkyTC test case", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.SetNextControlName("query");
                m_Query = EditorGUILayout.TextField(m_Query, EditorStyles.toolbarSearchField);
                var enter = Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
                if (GUILayout.Button("Search", GUILayout.Width(60)) || (enter && GUI.GetNameOfFocusedControl() == "query"))
                {
                    Search();
                    GUIUtility.ExitGUI();
                }
            }
            EditorGUILayout.LabelField("Number or words of the title; empty lists the first 50.", EditorStyles.miniLabel);

            if (m_Error != null)
            {
                EditorGUILayout.HelpBox(m_Error, MessageType.Warning);
                return;
            }
            if (m_Loading)
            {
                EditorGUILayout.LabelField("Loading…", EditorStyles.miniLabel);
                return;
            }
            if (m_Results.Length == 0)
            {
                EditorGUILayout.LabelField(string.IsNullOrEmpty(m_SearchedQuery) ? "The project has no test cases." : "No test case matches.", EditorStyles.miniLabel);
                return;
            }

            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            foreach (var testCase in m_Results)
            {
                var label = $"{m_Project.projectShortCode}-{testCase.testCaseNumber}  {testCase.title}";
                var section = string.IsNullOrEmpty(testCase.sectionPath) ? string.Empty : testCase.sectionPath;
                if (GUILayout.Button(new GUIContent(label, section), EditorStyles.label))
                {
                    m_OnPick(m_Project, testCase);
                    editorWindow.Close();
                    GUIUtility.ExitGUI();
                }
                if (!string.IsNullOrEmpty(section))
                    EditorGUILayout.LabelField("    " + section, EditorStyles.miniLabel);
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
