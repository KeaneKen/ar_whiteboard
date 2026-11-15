using System;
using UnityEngine;

[DefaultExecutionOrder(-50)]
public class BoardSettings : MonoBehaviour
{
    const string Prefix = "ar_whiteboard_";

    static readonly Color PanelBg = new Color(0.10f, 0.10f, 0.10f, 0.94f);
    static readonly Color RowBg = new Color(1f, 1f, 1f, 0.07f);
    static readonly Color OnColor = new Color(0.15f, 0.45f, 0.95f, 1f);
    static readonly Color OffColor = new Color(0.32f, 0.32f, 0.35f, 1f);
    static readonly Color CloseColor = new Color(0.86f, 0.22f, 0.22f, 1f);

    DrawingBoard board;
    BoardFeedback feedback;
    BoardObjects objects;
    BoardTasks tasks;
    Transform target;
    GUIStyle labelStyle, valueStyle, titleStyle;

    bool sound = true, haptics = true, smooth = true, reduceMotion, aim, showFps;
    int side;
    bool open;

    bool hasState, hasWritten;
    Vector3 rawPos, filteredPos, previousPos, writtenPos;
    Quaternion rawRot, filteredRot, previousRot, writtenRot;
    int stateFrame = -1;

    float fpsTimer, fpsValue, fpsLow = -1f;
    int fpsFrames, fpsWindows;

    public bool IsOpen => open;

    void Awake()
    {
        board = GetComponent<DrawingBoard>();
        target = transform.parent;

        sound = Get("sound", true);
        haptics = Get("haptics", true);
        smooth = Get("smooth", true);
        reduceMotion = Get("reduce_motion", false);
        aim = Get("aim", false);
        showFps = Get("fps", false);
        side = Mathf.Clamp(PlayerPrefs.GetInt(Prefix + "side", 0), -1, 1);
    }

    void Start()
    {
        feedback = GetComponent<BoardFeedback>();
        objects = GetComponent<BoardObjects>();
        tasks = GetComponent<BoardTasks>();
        Apply();
    }

    void OnEnable() { Application.onBeforeRender += StepPose; }
    void OnDisable() { Application.onBeforeRender -= StepPose; }

    static bool Get(string key, bool fallback)
    {
        return PlayerPrefs.GetInt(Prefix + key, fallback ? 1 : 0) != 0;
    }

    void SaveAll()
    {
        PlayerPrefs.SetInt(Prefix + "sound", sound ? 1 : 0);
        PlayerPrefs.SetInt(Prefix + "haptics", haptics ? 1 : 0);
        PlayerPrefs.SetInt(Prefix + "smooth", smooth ? 1 : 0);
        PlayerPrefs.SetInt(Prefix + "reduce_motion", reduceMotion ? 1 : 0);
        PlayerPrefs.SetInt(Prefix + "aim", aim ? 1 : 0);
        PlayerPrefs.SetInt(Prefix + "fps", showFps ? 1 : 0);
        PlayerPrefs.SetInt(Prefix + "side", side);
        PlayerPrefs.Save();
    }

    void Apply()
    {
        if (feedback != null)
        {
            feedback.soundOn = sound;
            feedback.hapticsOn = haptics;
            feedback.effectsOn = !reduceMotion;
        }
        if (objects != null) objects.reduceMotion = reduceMotion;
        board.AimMode = aim;
        board.ToolbarSide = side;
    }

    public void Open()
    {
        if (open || board.InputBlocked) return;
        open = true;
        board.BlockInput(true);
    }

    void Close()
    {
        open = false;
        board.BlockInput(false);
    }

    void Update()
    {
        StepPose();

        fpsFrames++;
        fpsTimer += Time.unscaledDeltaTime;
        if (fpsTimer >= 0.5f)
        {
            fpsValue = fpsFrames / fpsTimer;
            fpsWindows++;
            if (fpsWindows > 4 && (fpsLow < 0f || fpsValue < fpsLow)) fpsLow = fpsValue;
            fpsFrames = 0;
            fpsTimer = 0f;
        }
    }

    void LateUpdate() { StepPose(); }

    void StepPose()
    {
        if (target == null || board == null) return;
        Camera cam = board.arCamera != null ? board.arCamera : Camera.main;
        bool active = smooth && board.MarkerVisible && cam != null;
        if (!active)
        {
            hasState = false;
            hasWritten = false;
            return;
        }

        bool fresh = !hasWritten
                     || (target.position - writtenPos).sqrMagnitude > 1e-10f
                     || Quaternion.Angle(target.rotation, writtenRot) > 1e-3f;
        if (fresh)
        {
            rawPos = target.position;
            rawRot = target.rotation;
        }

        Transform c = cam.transform;
        Vector3 relPos = c.InverseTransformPoint(rawPos);
        Quaternion relRot = Quaternion.Inverse(c.rotation) * rawRot;

        if (!hasState)
        {
            hasState = true;
            stateFrame = Time.frameCount;
            previousPos = filteredPos = relPos;
            previousRot = filteredRot = relRot;
        }
        else
        {
            if (stateFrame != Time.frameCount)
            {
                stateFrame = Time.frameCount;
                previousPos = filteredPos;
                previousRot = filteredRot;
            }

            float move = (relPos - previousPos).magnitude / 0.04f;
            float turn = Quaternion.Angle(relRot, previousRot) / 3f;
            float baseAlpha = Mathf.Lerp(0.15f, 0.9f, Mathf.Clamp01(Mathf.Max(move, turn)));
            float alpha = 1f - Mathf.Pow(1f - baseAlpha, Mathf.Clamp(Time.unscaledDeltaTime * 60f, 0.25f, 4f));
            filteredPos = Vector3.Lerp(previousPos, relPos, alpha);
            filteredRot = Quaternion.Slerp(previousRot, relRot, alpha);
        }

        Quaternion worldRot = c.rotation * filteredRot;
        Vector3 worldPos = c.TransformPoint(filteredPos);

        target.SetPositionAndRotation(worldPos, worldRot);
        writtenPos = target.position;
        writtenRot = target.rotation;
        hasWritten = true;
    }

    void EnsureStyles()
    {
        if (labelStyle != null) return;
        labelStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft };
        labelStyle.normal.textColor = Color.white;
        valueStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        valueStyle.normal.textColor = Color.white;
        titleStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold };
        titleStyle.normal.textColor = Color.white;
    }

    bool Row(Rect r, string label, string value, Color valueColor)
    {
        board.GuiRound(r, RowBg, r.height * 0.25f);
        float pad = r.height * 0.3f;
        float valueWidth = r.height * 2.3f;
        GUI.Label(new Rect(r.x + pad, r.y, r.width - valueWidth - 2f * pad, r.height), label, labelStyle);
        Rect pill = new Rect(r.xMax - valueWidth - pad * 0.5f, r.y + r.height * 0.16f, valueWidth, r.height * 0.68f);
        board.GuiRound(pill, valueColor, pill.height * 0.45f);
        GUI.Label(pill, value, valueStyle);
        if (!GUI.Button(r, GUIContent.none, GUIStyle.none)) return false;
        if (feedback != null) feedback.Click();
        return true;
    }

    bool Toggle(Rect r, string label, bool value)
    {
        return Row(r, label, value ? "On" : "Off", value ? OnColor : OffColor);
    }

    void OnGUI()
    {
        if (board == null) return;
        EnsureStyles();
        float unit = board.UiUnit;

        if (showFps && !open && !board.UIHidden)
        {
            valueStyle.fontSize = Mathf.RoundToInt(unit * 0.2f);
            string text = Mathf.RoundToInt(fpsValue) + " fps";
            if (fpsLow > 0f) text += "  low " + Mathf.RoundToInt(fpsLow);
            float w = unit * 2.6f, h = unit * 0.5f;
            Rect box = new Rect(Screen.width - w - unit * 0.25f, Screen.height * 0.05f, w, h);
            board.GuiRound(box, PanelBg, h * 0.4f);
            GUI.Label(box, text, valueStyle);
        }

        if (!open) return;
        GUI.depth = -20;

        Color old = GUI.color;
        GUI.color = DrawingBoard.GuiColor(new Color(0f, 0f, 0f, 0.55f));
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = old;

        const int rows = 8;
        float width = Mathf.Min(Screen.width * 0.9f, unit * 6.4f);
        float rowH = Mathf.Min(unit * 0.66f, Screen.height * 0.88f / (rows + 1.6f));
        float gap = rowH * 0.14f;
        float pad = rowH * 0.4f;
        float height = pad * 2f + rowH * 1.1f + rows * rowH + (rows - 1) * gap;
        Rect panel = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);
        board.GuiRound(panel, PanelBg, rowH * 0.4f);

        labelStyle.fontSize = Mathf.RoundToInt(rowH * 0.36f);
        valueStyle.fontSize = Mathf.RoundToInt(rowH * 0.32f);
        titleStyle.fontSize = Mathf.RoundToInt(rowH * 0.44f);
        GUI.Label(new Rect(panel.x + pad, panel.y + pad * 0.6f, width - 2f * pad, rowH), "Settings", titleStyle);

        float y = panel.y + pad + rowH * 1.1f;
        Func<Rect> next = () =>
        {
            Rect r = new Rect(panel.x + pad, y, width - 2f * pad, rowH);
            y += rowH + gap;
            return r;
        };

        bool changed = false;
        if (Toggle(next(), "Sound", sound)) { sound = !sound; changed = true; }
        if (Toggle(next(), "Vibration", haptics)) { haptics = !haptics; changed = true; }
        if (Toggle(next(), "Steady board (less jitter)", smooth)) { smooth = !smooth; changed = true; }
        if (Toggle(next(), "Reduce motion effects", reduceMotion)) { reduceMotion = !reduceMotion; changed = true; }
        if (Toggle(next(), "Aim to draw (hold button)", aim)) { aim = !aim; changed = true; }
        if (Row(next(), "Toolbar side", side < 0 ? "Left" : side > 0 ? "Right" : "Center", OnColor))
        {
            side = side >= 1 ? -1 : side + 1;
            changed = true;
        }
        if (Toggle(next(), "Show frame rate", showFps))
        {
            showFps = !showFps;
            fpsLow = -1f;
            fpsWindows = 0;
            changed = true;
        }
        if (Row(next(), "Restart the tutorial", "Reset", OffColor))
        {
            if (tasks != null) tasks.ResetProgress();
        }
        float s = rowH * 0.85f;
        Rect xr = new Rect(panel.xMax - pad - s, panel.y + pad * 0.6f + (rowH - s) / 2f, s, s);
        board.GuiRound(xr, CloseColor, s * 0.3f);
        valueStyle.fontSize = Mathf.RoundToInt(s * 0.5f);
        GUI.Label(xr, "X", valueStyle);
        bool close = GUI.Button(xr, GUIContent.none, GUIStyle.none);
        if (close && feedback != null) feedback.Click();

        if (changed)
        {
            Apply();
            SaveAll();
        }

        GUI.Button(new Rect(0f, 0f, Screen.width, Screen.height), GUIContent.none, GUIStyle.none);
        if (close) Close();
    }
}