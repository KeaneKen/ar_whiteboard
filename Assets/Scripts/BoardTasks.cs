using UnityEngine;

public class BoardTasks : MonoBehaviour
{
    public enum Step { Draw = 0, PlaceNote = 1, WriteNote = 2, PinNote = 3 }

    [Header("Checklist")]
    public bool resetOnStart = false;
    public float doneMessageSeconds = 4f;

    const string PrefKey = "ar_whiteboard_tasks";
    const int AllDone = 15;

    static readonly string[] Titles =
    {
        "Draw on the board",
        "Place a sticky note",
        "Write on the note",
        "Pin the note",
    };

    static readonly string[] Hints =
    {
        "Drag your finger across the board.",
        "Open the color button and pick a note.",
        "Tap the note, then type or draw.",
        "Pick the pin, then tap on your note.",
    };

    static readonly Color PanelBg = new Color(0.10f, 0.10f, 0.10f, 0.82f);
    static readonly Color DoneColor = new Color(0.20f, 0.72f, 0.40f, 1f);
    static readonly Color RingColor = new Color(1f, 1f, 1f, 0.85f);
    static readonly Color NextColor = new Color(0.15f, 0.45f, 0.95f, 1f);

    DrawingBoard board;
    Texture2D checkIcon;
    GUIStyle titleStyle, rowStyle, hintStyle;
    int mask;
    bool collapsed;
    float finishedAt = -1f;

    bool Finished => mask == AllDone;
    bool Showing => !Finished || (finishedAt >= 0f && Time.unscaledTime - finishedAt < doneMessageSeconds);
    bool Usable => board != null && board.BoardActive && !board.UIHidden && !board.InputBlocked;

    void Awake()
    {
        board = GetComponent<DrawingBoard>();
        checkIcon = Resources.Load<Texture2D>("BoardIcons/done");
        if (resetOnStart) PlayerPrefs.DeleteKey(PrefKey);
        mask = PlayerPrefs.GetInt(PrefKey, 0) & AllDone;
    }

    public void Report(Step step)
    {
        int bit = 1 << (int)step;
        if ((mask & bit) != 0) return;
        mask |= bit;
        PlayerPrefs.SetInt(PrefKey, mask);
        PlayerPrefs.Save();

        if (Finished)
        {
            finishedAt = Time.unscaledTime;
            collapsed = false;
            if (board != null && board.Feedback != null) board.Feedback.Poof();
        }
        else if (board != null && board.Feedback != null)
        {
            board.Feedback.Drop();
        }
    }

    public void ResetProgress()
    {
        mask = 0;
        finishedAt = -1f;
        collapsed = false;
        PlayerPrefs.DeleteKey(PrefKey);
    }

    public bool Covers(Vector2 guiPoint)
    {
        return Usable && Showing && PanelRect().Contains(guiPoint);
    }

    int DoneCount()
    {
        int n = 0;
        for (int i = 0; i < Titles.Length; i++) if ((mask & (1 << i)) != 0) n++;
        return n;
    }

    int NextStep()
    {
        for (int i = 0; i < Titles.Length; i++) if ((mask & (1 << i)) == 0) return i;
        return -1;
    }

    Rect PanelRect()
    {
        float unit = board.UiUnit;
        float margin = unit * 0.25f;
        float top = Screen.height * 0.05f;

        if (Finished) return new Rect(margin, top, Mathf.Min(Screen.width - 2f * margin, unit * 5.4f), unit * 0.9f);
        if (collapsed) return new Rect(margin, top, unit * 2.4f, unit * 0.6f);

        float width = Mathf.Min(Screen.width - 2f * margin, unit * 5.4f);
        float height = unit * (0.62f + 0.46f * Titles.Length + 0.62f);
        return new Rect(margin, top, width, height);
    }

    void EnsureStyles(float unit)
    {
        if (titleStyle == null)
        {
            titleStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold };
            titleStyle.normal.textColor = Color.white;
            rowStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft };
            rowStyle.normal.textColor = Color.white;
            hintStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, wordWrap = true };
            hintStyle.normal.textColor = new Color(1f, 1f, 1f, 0.75f);
        }
        titleStyle.fontSize = Mathf.RoundToInt(unit * 0.25f);
        rowStyle.fontSize = Mathf.RoundToInt(unit * 0.23f);
        hintStyle.fontSize = Mathf.RoundToInt(unit * 0.19f);
    }

    void OnGUI()
    {
        if (!Usable || !Showing) return;

        float unit = board.UiUnit;
        EnsureStyles(unit);
        Rect panel = PanelRect();
        float pad = unit * 0.22f;
        board.GuiRound(panel, PanelBg, unit * 0.22f);

        if (Finished)
        {
            Rect dot = new Rect(panel.x + pad, panel.center.y - unit * 0.2f, unit * 0.4f, unit * 0.4f);
            DrawMark(dot, true, false);
            GUI.Label(new Rect(dot.xMax + pad * 0.6f, panel.y, panel.width - dot.width - pad * 2.6f, panel.height),
                      "All done!", titleStyle);
            return;
        }

        if (collapsed)
        {
            GUI.Label(new Rect(panel.x + pad, panel.y, panel.width - 2f * pad, panel.height),
                      "Tasks " + DoneCount() + "/" + Titles.Length, titleStyle);
        }
        else
        {
            GUI.Label(new Rect(panel.x + pad, panel.y, panel.width - 2f * pad, unit * 0.62f),
                      "Try this  " + DoneCount() + "/" + Titles.Length, titleStyle);

            int next = NextStep();
            float rowH = unit * 0.46f;
            float y = panel.y + unit * 0.62f;
            for (int i = 0; i < Titles.Length; i++)
            {
                bool done = (mask & (1 << i)) != 0;
                float d = unit * 0.3f;
                Rect dot = new Rect(panel.x + pad, y + (rowH - d) / 2f, d, d);
                DrawMark(dot, done, i == next);

                Color textColor = rowStyle.normal.textColor;
                rowStyle.normal.textColor = done ? new Color(1f, 1f, 1f, 0.5f) : Color.white;
                rowStyle.fontStyle = i == next ? FontStyle.Bold : FontStyle.Normal;
                GUI.Label(new Rect(dot.xMax + pad * 0.6f, y, panel.xMax - dot.xMax - pad * 1.6f, rowH), Titles[i], rowStyle);
                rowStyle.normal.textColor = textColor;
                y += rowH;
            }

            if (next >= 0)
            {
                GUI.Label(new Rect(panel.x + pad, y, panel.width - 2f * pad, panel.yMax - y - pad * 0.3f),
                          Hints[next], hintStyle);
            }
        }

        if (GUI.Button(panel, GUIContent.none, GUIStyle.none))
        {
            collapsed = !collapsed;
            if (board.Feedback != null) board.Feedback.Click();
        }
    }

    void DrawMark(Rect r, bool done, bool next)
    {
        if (done)
        {
            board.GuiCircle(r, DoneColor);
            if (checkIcon != null)
            {
                float inset = r.width * 0.2f;
                board.GuiIcon(new Rect(r.x + inset, r.y + inset, r.width - 2f * inset, r.height - 2f * inset),
                              checkIcon, Color.white);
            }
            return;
        }

        board.GuiCircle(r, next ? NextColor : RingColor);
        float ring = r.width * 0.16f;
        board.GuiCircle(new Rect(r.x + ring, r.y + ring, r.width - 2f * ring, r.height - 2f * ring),
                        new Color(0.12f, 0.12f, 0.12f, 1f));
    }
}