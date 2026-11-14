using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(DrawingBoard))]
public class BoardObjects : MonoBehaviour
{
    public enum Kind { Note = 0, Magnet = 1, Ruler = 2 }

    [Serializable]
    public class NoteStroke { public List<Vector2> pts = new List<Vector2>(); }

    [Serializable]
    public class Item
    {
        public int kind;
        public float u, v, angle;
        public int tint;
        public bool home;
        public string text = "";
        public List<NoteStroke> strokes = new List<NoteStroke>();
    }

    [Serializable]
    public class SaveData { public List<Item> items = new List<Item>(); }

    class Obj
    {
        public Item item;
        public Transform body;
        public Renderer face, rim;
        public bool busy;
        public Transform content;
        public TextMesh label;
        public readonly List<GameObject> lines = new List<GameObject>();
    }

    enum Mode { Normal, Placing, Editing }
    enum Phase { Idle, Pending, Holding }
    enum Look { Available, Targeted, Held, HeldOffBoard }

    [Header("Grab")]
    public float holdSeconds = 0.3f;
    public float liftHeight = 0.03f;
    public int maxNotes = 20;
    public int maxMagnets = 8;
    public int maxRulers = 3;

    const float Thickness = 0.004f;
    const float BaseHeight = 0.010f;
    const float LayerStep = 0.0005f;
    const float RimWidth = 0.006f;
    const float GrabMargin = 0.03f;
    const float FallGravity = 6f;
    const float SnapDistance = 0.03f;
    const float RulerTurn = 45f;
    const float NoteSize = 0.15f;
    const float NotePadding = 0.012f;
    const int WrapColumns = 16;
    const int WrapLines = 7;
    const int TextLimit = 110;
    const int CanvasSize = 384;
    const float CanvasPen = CanvasSize * 0.011f;

    static readonly Vector2[] Sizes =
    {
        new Vector2(NoteSize, NoteSize),
        new Vector2(0.07f, 0.07f),
        new Vector2(0.30f, 0.05f),
    };

    public static readonly Color[] NoteTints =
    {
        new Color(1.00f, 0.86f, 0.25f),
        new Color(1.00f, 0.62f, 0.75f),
        new Color(0.55f, 0.90f, 0.70f),
        new Color(0.60f, 0.80f, 1.00f),
    };

    public static readonly Color MagnetColor = new Color(0.90f, 0.20f, 0.25f);
    public static readonly Color RulerColor = new Color(0.25f, 0.55f, 0.95f);
    static readonly Color RimIdle = new Color(0.20f, 0.20f, 0.22f);
    static readonly Color RimTargeted = Color.white;
    static readonly Color RimHeld = new Color(0.15f, 0.45f, 0.95f);
    static readonly Color RimOffBoard = new Color(0.90f, 0.15f, 0.15f);
    static readonly Color ShadowColor = new Color(0.68f, 0.73f, 0.75f);
    static readonly Color NoteInk = new Color(0.10f, 0.10f, 0.12f);
    static readonly Color PanelBg = new Color(0.10f, 0.10f, 0.10f, 0.85f);
    static readonly Color ButtonBg = new Color(0.22f, 0.22f, 0.24f, 1f);
    static readonly Color ButtonActive = new Color(0.15f, 0.45f, 0.95f, 1f);
    static readonly Color ButtonDanger = new Color(0.80f, 0.18f, 0.18f, 1f);

    DrawingBoard board;
    Transform root;
    Transform shadow;
    Font font;
    MaterialPropertyBlock block;
    readonly List<Obj> objects = new List<Obj>();

    Mode mode = Mode.Normal;
    Phase phase = Phase.Idle;
    Obj target;
    bool wasDown;
    bool onBoard;
    Vector2 pressPos;
    float pressTime;
    Vector2 grabOffset;
    Vector2 heldUV;

    int placingKind;
    int placingTint;
    bool denied;
    bool waitRelease;
    Obj editing;
    bool typing;
    TouchScreenKeyboard keyboard;
    NoteStroke liveStroke;
    GUIStyle noteStyle, buttonStyle, hintStyle;
    Texture2D canvas;
    Color32[] canvasPixels;
    bool canvasDirty;
    Texture2D[] editorIcons;

    string SavePath => Path.Combine(Application.persistentDataPath, "objects.json");

    float BoardW => Mathf.Abs(transform.lossyScale.x) * 10f;
    float BoardH => Mathf.Abs(transform.lossyScale.z) * 10f;

    void Awake()
    {
        board = GetComponent<DrawingBoard>();
        block = new MaterialPropertyBlock();
        font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        canvas = new Texture2D(CanvasSize, CanvasSize, TextureFormat.RGBA32, false);
        canvas.wrapMode = TextureWrapMode.Clamp;
        canvas.filterMode = FilterMode.Bilinear;
        canvasPixels = new Color32[CanvasSize * CanvasSize];

        string[] names = { "type", "draw", "undo", "trash", "done" };
        editorIcons = new Texture2D[names.Length];
        for (int i = 0; i < names.Length; i++) editorIcons[i] = Resources.Load<Texture2D>("BoardIcons/" + names[i]);
    }

    void OnDestroy()
    {
        if (canvas != null) Destroy(canvas);
    }

    void Stamp(Vector2 p)
    {
        float cx = p.x * CanvasSize, cy = p.y * CanvasSize, r = CanvasPen;
        int x0 = Mathf.Max(0, Mathf.FloorToInt(cx - r - 1f)), x1 = Mathf.Min(CanvasSize - 1, Mathf.CeilToInt(cx + r + 1f));
        int y0 = Mathf.Max(0, Mathf.FloorToInt(cy - r - 1f)), y1 = Mathf.Min(CanvasSize - 1, Mathf.CeilToInt(cy + r + 1f));
        Color32 ink = NoteInk;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                float a = Mathf.Clamp01(r + 0.5f - Mathf.Sqrt(dx * dx + dy * dy));
                if (a <= 0f) continue;
                int index = y * CanvasSize + x;
                byte alpha = (byte)(a * 255f);
                if (alpha <= canvasPixels[index].a) continue;
                ink.a = alpha;
                canvasPixels[index] = ink;
            }
        }
        canvasDirty = true;
    }

    void StampSegment(Vector2 a, Vector2 b)
    {
        float pixels = (b - a).magnitude * CanvasSize;
        int steps = Mathf.Max(1, Mathf.CeilToInt(pixels / Mathf.Max(1f, CanvasPen * 0.5f)));
        for (int i = 0; i <= steps; i++) Stamp(Vector2.Lerp(a, b, i / (float)steps));
    }

    void RedrawCanvas()
    {
        Array.Clear(canvasPixels, 0, canvasPixels.Length);
        if (editing != null)
        {
            foreach (NoteStroke s in editing.item.strokes)
            {
                if (s == null || s.pts == null) continue;
                for (int i = 1; i < s.pts.Count; i++) StampSegment(s.pts[i - 1], s.pts[i]);
            }
        }
        canvasDirty = true;
    }

    void Start()
    {
        Transform parent = transform.parent != null ? transform.parent : transform;
        root = new GameObject("BoardObjects").transform;
        root.SetParent(parent, false);

        Load();
    }

    public bool IsOverNote(Vector2 uv)
    {
        float w = BoardW, h = BoardH;
        foreach (Obj o in objects)
        {
            if (o.item.kind != (int)Kind.Note) continue;
            if (Mathf.Abs(uv.x - o.item.u) * w <= NoteSize / 2f && Mathf.Abs(uv.y - o.item.v) * h <= NoteSize / 2f)
                return true;
        }
        return false;
    }

    public void BeginPlacing(int kind, int tint)
    {
        if (mode != Mode.Normal || phase != Phase.Idle) return;
        int limit = kind == (int)Kind.Note ? maxNotes : kind == (int)Kind.Magnet ? maxMagnets : maxRulers;
        if (Count(kind) >= limit) return;
        placingKind = kind;
        placingTint = tint;
        mode = Mode.Placing;
        board.BlockInput(true);
    }

    public Vector3 SnapToRuler(Vector3 world)
    {
        Vector3 local = transform.InverseTransformPoint(world);
        Vector2 uv = new Vector2((local.x + 5f) / 10f, (local.z + 5f) / 10f);
        float w = BoardW, h = BoardH;
        Vector2 size = Sizes[(int)Kind.Ruler];

        foreach (Obj o in objects)
        {
            if (o.item.kind != (int)Kind.Ruler || o.busy || o == target) continue;
            Vector2 p = ToLocal(o, uv, w, h);
            if (Mathf.Abs(p.x) > size.x / 2f + 0.02f) continue;
            if (Mathf.Abs(p.y) > size.y / 2f + SnapDistance) continue;

            p.y = (p.y >= 0f ? 1f : -1f) * (size.y / 2f + RimWidth + 0.003f);
            float rad = o.item.angle * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);
            float dx = p.x * cos + p.y * sin;
            float dz = -p.x * sin + p.y * cos;
            return BoardPoint(new Vector2(o.item.u + dx / w, o.item.v + dz / h), 0f);
        }
        return world;
    }

    public void ResetAll()
    {
        if (root == null) return;
        StopAllCoroutines();
        CloseKeyboard();
        if (shadow != null) { Destroy(shadow.gameObject); shadow = null; }
        target = null;
        editing = null;
        liveStroke = null;
        phase = Phase.Idle;
        mode = Mode.Normal;
        waitRelease = false;
        board.UIHidden = false;
        board.BlockInput(false);

        foreach (Obj o in objects) DestroyObj(o);
        objects.Clear();
        Save();
    }

    void Update()
    {
        bool visible = board.MarkerVisible || !board.requireMarker;
        if (root.gameObject.activeSelf != visible) root.gameObject.SetActive(visible);

        Vector2 sp;
        bool down = TryGetPointer(out sp);
        bool began = down && !wasDown;
        wasDown = down;

        if (mode == Mode.Editing) { UpdateEditing(sp, down, began); return; }
        if (mode == Mode.Placing) { UpdatePlacing(sp, began); return; }

        if (waitRelease)
        {
            if (down) return;
            waitRelease = false;
            board.BlockInput(false);
            return;
        }

        if (phase == Phase.Idle)
        {
            if (!began || !visible || !board.BoardActive || board.IsOverUI(sp)) return;
            Vector2 uv;
            if (!RayToUV(sp, out uv)) return;
            Obj hit = Pick(uv);
            if (hit == null) return;

            target = hit;
            denied = false;
            pressPos = sp;
            pressTime = Time.unscaledTime;
            phase = Phase.Pending;
            SetLook(target, Look.Targeted);
            return;
        }

        if (phase == Phase.Pending)
        {
            bool moved = down && (sp - pressPos).magnitude > Screen.height * 0.02f;
            if (!down || moved || !board.BoardActive)
            {
                Obj tapped = target;
                SetLook(target, Look.Available);
                target = null;
                phase = Phase.Idle;
                if (down || denied || !board.BoardActive) return;

                if (tapped.item.kind == (int)Kind.Note) OpenEditor(tapped);
                else if (tapped.item.kind == (int)Kind.Ruler)
                {
                    tapped.item.angle = Mathf.Repeat(tapped.item.angle + RulerTurn, 180f);
                    Refresh();
                    Save();
                }
                return;
            }
            if (denied || Time.unscaledTime - pressTime < holdSeconds) return;

            if (IsPinned(target))
            {
                denied = true;
                SetLook(target, Look.HeldOffBoard);
                return;
            }
            BeginHold(sp);
            return;
        }

        if (down && board.BoardActive) DragTo(sp);
        else Release();
    }

    void UpdatePlacing(Vector2 sp, bool began)
    {
        if (!board.BoardActive) { EndPlacing(); return; }
        if (!began) return;
        if (board.IsOverUI(sp)) { EndPlacing(); return; }

        Vector2 uv;
        if (!RayToUV(sp, out uv)) return;
        if (uv.x < 0f || uv.x > 1f || uv.y < 0f || uv.y > 1f) return;

        Vector2 size = Sizes[placingKind];
        float halfU = size.x / 2f / BoardW, halfV = size.y / 2f / BoardH;
        uv.x = Mathf.Clamp(uv.x, halfU, 1f - halfU);
        uv.y = Mathf.Clamp(uv.y, halfV, 1f - halfV);

        Item item = new Item
        {
            kind = placingKind,
            u = uv.x,
            v = uv.y,
            angle = placingKind == (int)Kind.Note ? UnityEngine.Random.Range(-3f, 3f) : 0f,
            tint = placingTint
        };
        Obj placed = Build(item);
        Refresh();
        Save();

        mode = Mode.Normal;
        if (placingKind == (int)Kind.Note) OpenEditor(placed);
        else waitRelease = true;
    }

    void EndPlacing()
    {
        mode = Mode.Normal;
        board.BlockInput(false);
    }

    bool TryGetPointer(out Vector2 pos)
    {
        pos = default(Vector2);
        if (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.isPressed)
        {
            pos = Touchscreen.current.primaryTouch.position.ReadValue();
            return true;
        }
        if (Mouse.current != null && Mouse.current.leftButton.isPressed)
        {
            pos = Mouse.current.position.ReadValue();
            return true;
        }
        return false;
    }

    Vector3 BoardPoint(Vector2 uv, float height)
    {
        Vector3 local = new Vector3(uv.x * 10f - 5f, 0f, uv.y * 10f - 5f);
        return transform.TransformPoint(local) + transform.up * height;
    }

    bool RayToUV(Vector2 screenPos, out Vector2 uv)
    {
        uv = default(Vector2);
        Camera cam = board.arCamera != null ? board.arCamera : Camera.main;
        if (cam == null) return false;

        Plane plane = new Plane(transform.up, transform.position);
        Ray ray = cam.ScreenPointToRay(screenPos);
        float distance;
        if (!plane.Raycast(ray, out distance)) return false;

        Vector3 local = transform.InverseTransformPoint(ray.GetPoint(distance));
        uv = new Vector2((local.x + 5f) / 10f, (local.z + 5f) / 10f);
        return true;
    }

    Vector2 ToLocal(Obj o, Vector2 uv, float w, float h)
    {
        float dx = (uv.x - o.item.u) * w;
        float dz = (uv.y - o.item.v) * h;
        float rad = o.item.angle * Mathf.Deg2Rad;
        float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);
        return new Vector2(dx * cos - dz * sin, dx * sin + dz * cos);
    }

    bool IsPinned(Obj note)
    {
        if (note == null || note.item.kind != (int)Kind.Note) return false;
        float w = BoardW, h = BoardH;
        foreach (Obj o in objects)
        {
            if (o.item.kind != (int)Kind.Magnet || o.busy || o == target) continue;
            if (Mathf.Abs(o.item.u - note.item.u) * w <= NoteSize / 2f &&
                Mathf.Abs(o.item.v - note.item.v) * h <= NoteSize / 2f) return true;
        }
        return false;
    }

    void Raise(Obj o)
    {
        objects.Remove(o);
        if (o.item.kind != (int)Kind.Note) { objects.Add(o); return; }
        int index = objects.Count;
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i].item.kind != (int)Kind.Note) { index = i; break; }
        }
        objects.Insert(index, o);
    }

    int Count(int kind)
    {
        int n = 0;
        foreach (Obj o in objects) if (o.item.kind == kind) n++;
        return n;
    }

    Obj Pick(Vector2 uv)
    {
        float w = BoardW, h = BoardH;
        for (int i = objects.Count - 1; i >= 0; i--)
        {
            Obj o = objects[i];
            if (o.busy) continue;
            Vector2 size = Sizes[o.item.kind];
            float margin = o.item.kind == (int)Kind.Note ? 0f : GrabMargin;
            Vector2 p = ToLocal(o, uv, w, h);
            if (Mathf.Abs(p.x) <= size.x / 2f + margin && Mathf.Abs(p.y) <= size.y / 2f + margin) return o;
        }
        return null;
    }

    void BeginHold(Vector2 sp)
    {
        board.BlockInput(true);
        phase = Phase.Holding;

        Vector2 itemUV = new Vector2(target.item.u, target.item.v);
        Vector2 uv;
        grabOffset = RayToUV(sp, out uv) ? itemUV - uv : Vector2.zero;
        heldUV = itemUV;
        target.item.home = false;
        Raise(target);

        Renderer shadowRenderer;
        PrimitiveType prim = target.item.kind == (int)Kind.Magnet ? PrimitiveType.Cylinder : PrimitiveType.Cube;
        shadow = MakePart(prim, ShadowColor, out shadowRenderer);
        shadow.name = "Shadow";
        shadow.SetParent(root, false);
        Vector2 size = Sizes[target.item.kind];
        float unitY = prim == PrimitiveType.Cylinder ? 0.0005f : 0.001f;
        SetWorldScale(shadow, new Vector3(size.x, unitY, size.y));

        DragTo(sp);
    }

    void DragTo(Vector2 sp)
    {
        Vector2 uv;
        if (RayToUV(sp, out uv)) heldUV = uv + grabOffset;

        onBoard = heldUV.x >= 0f && heldUV.x <= 1f && heldUV.y >= 0f && heldUV.y <= 1f;
        SetLook(target, onBoard ? Look.Held : Look.HeldOffBoard);
        Pose(target, heldUV, liftHeight, 1.08f);

        if (shadow != null)
        {
            Vector2 shadowUV = heldUV + new Vector2(0.012f / BoardW, -0.012f / BoardH);
            shadow.gameObject.SetActive(onBoard);
            shadow.SetPositionAndRotation(BoardPoint(shadowUV, 0.0025f),
                                          transform.rotation * Quaternion.Euler(0f, target.item.angle, 0f));
        }
    }

    void Release()
    {
        Obj o = target;
        target = null;
        phase = Phase.Idle;
        board.BlockInput(false);
        if (shadow != null) { Destroy(shadow.gameObject); shadow = null; }

        if (onBoard)
        {
            o.item.u = heldUV.x;
            o.item.v = heldUV.y;
            StartCoroutine(Settle(o));
        }
        else
        {
            StartCoroutine(Fall(o));
        }
    }

    IEnumerator Settle(Obj o)
    {
        o.busy = true;
        Vector2 uv = new Vector2(o.item.u, o.item.v);
        const float duration = 0.12f;
        for (float t = 0f; t < duration; t += Time.deltaTime)
        {
            float k = 1f - t / duration;
            Pose(o, uv, liftHeight * k, 1f + 0.08f * k);
            yield return null;
        }
        o.busy = false;
        SetLook(o, Look.Available);
        Refresh();
        Save();
    }

    IEnumerator Fall(Obj o)
    {
        o.busy = true;
        SetLook(o, Look.Available);
        Vector3 start = o.body.position;
        const float duration = 0.5f;
        for (float t = 0f; t < duration; t += Time.deltaTime)
        {
            Vector3 p = start - transform.forward * (0.5f * FallGravity * t * t);
            o.body.position = p;
            if (o.content != null) o.content.position = p;
            yield return null;
        }

        objects.Remove(o);
        DestroyObj(o);
        Refresh();
        Save();
    }

    void Pose(Obj o, Vector2 uv, float lift, float scale)
    {
        int layer = Mathf.Max(0, objects.IndexOf(o));
        Vector3 position = BoardPoint(uv, BaseHeight + layer * LayerStep + lift);
        Quaternion rotation = transform.rotation * Quaternion.Euler(0f, o.item.angle, 0f);

        o.body.SetPositionAndRotation(position, rotation);
        Vector2 size = Sizes[o.item.kind];
        float unitY = o.item.kind == (int)Kind.Magnet ? Thickness / 2f : Thickness;
        SetWorldScale(o.body, new Vector3(size.x * scale, unitY, size.y * scale));

        if (o.content != null)
        {
            o.content.SetPositionAndRotation(position, rotation);
            SetWorldScale(o.content, Vector3.one * scale);
        }
    }

    void Refresh()
    {
        foreach (Obj o in objects)
        {
            if (o.busy || o == target) continue;
            Pose(o, new Vector2(o.item.u, o.item.v), 0f, 1f);
        }
    }

    Rect EditorRect()
    {
        float side = Mathf.Min(Screen.width * 0.86f, Screen.height * 0.42f);
        return new Rect((Screen.width - side) / 2f, Screen.height * 0.07f, side, side);
    }

    void OpenEditor(Obj note)
    {
        editing = note;
        if (editing.item.text == null) editing.item.text = "";
        if (editing.item.strokes == null) editing.item.strokes = new List<NoteStroke>();
        liveStroke = null;
        RedrawCanvas();
        mode = Mode.Editing;
        board.BlockInput(true);
        board.UIHidden = true;
        SetTyping(true);
    }

    void CloseEditor(bool delete)
    {
        CloseKeyboard();
        typing = false;
        liveStroke = null;
        Obj o = editing;
        editing = null;
        mode = Mode.Normal;
        board.UIHidden = false;
        board.BlockInput(false);

        if (o != null)
        {
            if (delete)
            {
                objects.Remove(o);
                DestroyObj(o);
                Refresh();
            }
            else
            {
                RebuildContent(o);
            }
        }
        Save();
    }

    void SetTyping(bool on)
    {
        typing = on;
        liveStroke = null;
        CloseKeyboard();
        if (on && TouchScreenKeyboard.isSupported)
        {
            keyboard = TouchScreenKeyboard.Open(editing.item.text, TouchScreenKeyboardType.Default, true, true);
            if (keyboard != null) keyboard.characterLimit = TextLimit;
        }
    }

    void CloseKeyboard()
    {
        if (keyboard != null) keyboard.active = false;
        keyboard = null;
    }

    void UpdateEditing(Vector2 sp, bool down, bool began)
    {
        if (editing == null) { CloseEditor(false); return; }

        if (typing)
        {
            if (keyboard != null)
            {
                string t = keyboard.text ?? "";
                if (t.Length > TextLimit) t = t.Substring(0, TextLimit);
                editing.item.text = t;
                if (keyboard.status != TouchScreenKeyboard.Status.Visible)
                {
                    keyboard = null;
                    typing = false;
                }
            }
            return;
        }

        Rect n = EditorRect();
        Vector2 gui = new Vector2(sp.x, Screen.height - sp.y);
        bool inside = down && n.Contains(gui);

        if (inside)
        {
            Vector2 p = new Vector2((gui.x - n.x) / n.width, 1f - (gui.y - n.y) / n.height);
            if (began) liveStroke = new NoteStroke();
            if (liveStroke != null)
            {
                int c = liveStroke.pts.Count;
                if (c == 0)
                {
                    liveStroke.pts.Add(p);
                    Stamp(p);
                }
                else if ((liveStroke.pts[c - 1] - p).magnitude > 0.004f)
                {
                    StampSegment(liveStroke.pts[c - 1], p);
                    liveStroke.pts.Add(p);
                }
            }
        }
        else if (liveStroke != null)
        {
            if (liveStroke.pts.Count >= 2)
            {
                liveStroke.pts = DrawingBoard.Smooth(liveStroke.pts, 2);
                editing.item.strokes.Add(liveStroke);
            }
            liveStroke = null;
            RedrawCanvas();
        }

        if (canvasDirty)
        {
            canvas.SetPixels32(canvasPixels);
            canvas.Apply(false);
            canvasDirty = false;
        }
    }

    static string Wrap(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        StringBuilder sb = new StringBuilder();
        int lines = 0;
        foreach (string paragraph in text.Replace("\r", "").Split('\n'))
        {
            string line = "";
            foreach (string raw in paragraph.Split(' '))
            {
                string word = raw;
                while (word.Length > WrapColumns)
                {
                    if (line.Length > 0) { if (!AddLine(sb, line, ref lines)) return sb.ToString(); line = ""; }
                    if (!AddLine(sb, word.Substring(0, WrapColumns), ref lines)) return sb.ToString();
                    word = word.Substring(WrapColumns);
                }
                if (line.Length == 0) line = word;
                else if (line.Length + 1 + word.Length <= WrapColumns) line += " " + word;
                else
                {
                    if (!AddLine(sb, line, ref lines)) return sb.ToString();
                    line = word;
                }
            }
            if (!AddLine(sb, line, ref lines)) return sb.ToString();
        }
        return sb.ToString();
    }

    static bool AddLine(StringBuilder sb, string line, ref int lines)
    {
        if (lines >= WrapLines) return false;
        if (lines > 0) sb.Append('\n');
        sb.Append(line);
        lines++;
        return true;
    }

    void EnsureStyles()
    {
        if (noteStyle != null) return;
        noteStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, wordWrap = false, richText = false };
        noteStyle.normal.textColor = NoteInk;
        buttonStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        buttonStyle.normal.textColor = Color.white;
        hintStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = true };
        hintStyle.normal.textColor = Color.white;
    }

    bool IconButton(Rect r, int index, string fallback, Color bg, float iconAlpha)
    {
        board.GuiCircle(r, bg);
        Texture2D icon = editorIcons[index];
        if (icon != null)
        {
            float inset = r.width * 0.24f;
            Rect inner = new Rect(r.x + inset, r.y + inset, r.width - 2f * inset, r.height - 2f * inset);
            board.GuiIcon(inner, icon, new Color(1f, 1f, 1f, iconAlpha));
        }
        else
        {
            GUI.Label(r, fallback, buttonStyle);
        }
        return GUI.Button(r, GUIContent.none, GUIStyle.none);
    }

    void OnGUI()
    {
        if (mode == Mode.Normal) return;
        GUI.depth = -10;
        EnsureStyles();
        float unit = board.UiUnit;

        if (mode == Mode.Placing)
        {
            float w = Mathf.Min(Screen.width * 0.86f, unit * 7f);
            float h = unit * 1.3f;
            Rect box = new Rect((Screen.width - w) / 2f, Screen.height * 0.10f, w, h);
            hintStyle.fontSize = Mathf.RoundToInt(unit * 0.30f);
            board.GuiRound(box, PanelBg, h * 0.25f);
            string what = placingKind == (int)Kind.Note ? "note" : placingKind == (int)Kind.Magnet ? "magnet" : "ruler";
            GUI.Label(box, "Tap the board to place the " + what, hintStyle);
            return;
        }

        if (editing == null) return;

        Color old = GUI.color;
        GUI.color = DrawingBoard.GuiColor(new Color(0f, 0f, 0f, 0.6f));
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = old;

        Rect n = EditorRect();
        board.GuiRound(n, NoteTints[Mathf.Abs(editing.item.tint) % NoteTints.Length], n.width * 0.04f);

        float pad = n.width * NotePadding / NoteSize;
        Rect inner = new Rect(n.x + pad, n.y + pad, n.width - 2f * pad, n.height - 2f * pad);
        noteStyle.fontSize = Mathf.RoundToInt(n.width / 11f);

        if (typing && !TouchScreenKeyboard.isSupported)
        {
            string t = GUI.TextArea(inner, editing.item.text, TextLimit, noteStyle);
            editing.item.text = t;
        }
        else if (string.IsNullOrEmpty(editing.item.text) && editing.item.strokes.Count == 0 && liveStroke == null)
        {
            Color textColor = noteStyle.normal.textColor;
            noteStyle.normal.textColor = new Color(0f, 0f, 0f, 0.35f);
            GUI.Label(inner, typing ? "Type something" : "Draw here", noteStyle);
            noteStyle.normal.textColor = textColor;
        }
        else
        {
            GUI.Label(inner, Wrap(editing.item.text), noteStyle);
        }

        if (canvasDirty)
        {
            canvas.SetPixels32(canvasPixels);
            canvas.Apply(false);
            canvasDirty = false;
        }
        GUI.DrawTexture(n, canvas);

        string[] labels = { "Type", "Draw", "Undo", "Delete", "Done" };
        int count = labels.Length;
        float size = Mathf.Min(unit, n.width / (count + (count - 1) * 0.3f));
        float gap = (n.width - size * count) / (count - 1);
        float by = n.yMax + size * 0.3f;
        buttonStyle.fontSize = Mathf.RoundToInt(size * 0.24f);
        bool pinned = IsPinned(editing);

        for (int i = 0; i < count; i++)
        {
            Rect r = new Rect(n.x + i * (size + gap), by, size, size);
            Color bg = ButtonBg;
            if ((i == 0 && typing) || (i == 1 && !typing)) bg = ButtonActive;
            if (i == 3 && !pinned) bg = ButtonDanger;
            float alpha = i == 3 && pinned ? 0.3f : 1f;
            if (!IconButton(r, i, labels[i], bg, alpha)) continue;
            if (i == 3 && pinned) break;

            if (i == 0) SetTyping(true);
            else if (i == 1) SetTyping(false);
            else if (i == 2)
            {
                List<NoteStroke> strokes = editing.item.strokes;
                if (strokes.Count > 0) strokes.RemoveAt(strokes.Count - 1);
                RedrawCanvas();
            }
            else if (i == 3) CloseEditor(true);
            else CloseEditor(false);
            break;
        }
    }

    Color FaceColor(Item item)
    {
        if (item.kind == (int)Kind.Magnet) return MagnetColor;
        if (item.kind == (int)Kind.Ruler) return RulerColor;
        return NoteTints[Mathf.Abs(item.tint) % NoteTints.Length];
    }

    void SetLook(Obj o, Look look)
    {
        if (o == null || o.body == null) return;
        Color face = FaceColor(o.item);
        if (look == Look.Targeted) face = Color.Lerp(face, Color.white, 0.35f);
        Paint(o.face, face);

        Color rim = RimIdle;
        if (look == Look.Targeted) rim = RimTargeted;
        else if (look == Look.Held) rim = RimHeld;
        else if (look == Look.HeldOffBoard) rim = RimOffBoard;
        Paint(o.rim, rim);
    }

    void Paint(Renderer r, Color c)
    {
        block.SetColor("_BaseColor", c);
        block.SetColor("_Color", c);
        r.SetPropertyBlock(block);
    }

    Transform MakePart(PrimitiveType type, Color color, out Renderer r)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        Collider col = go.GetComponent<Collider>();
        if (col != null) Destroy(col);
        r = go.GetComponent<Renderer>();
        r.sharedMaterial = board.lineMaterial;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        Paint(r, color);
        return go.transform;
    }

    void SetWorldScale(Transform t, Vector3 world)
    {
        Vector3 p = t.parent != null ? t.parent.lossyScale : Vector3.one;
        t.localScale = new Vector3(
            world.x / Mathf.Max(Mathf.Abs(p.x), 1e-6f),
            world.y / Mathf.Max(Mathf.Abs(p.y), 1e-6f),
            world.z / Mathf.Max(Mathf.Abs(p.z), 1e-6f));
    }

    Obj Build(Item item)
    {
        bool round = item.kind == (int)Kind.Magnet;
        PrimitiveType prim = round ? PrimitiveType.Cylinder : PrimitiveType.Cube;
        Vector2 size = Sizes[item.kind];

        Obj o = new Obj { item = item };
        o.body = MakePart(prim, FaceColor(item), out o.face);
        o.body.name = ((Kind)item.kind).ToString();
        o.body.SetParent(root, false);

        Transform rim = MakePart(prim, RimIdle, out o.rim);
        rim.name = "Rim";
        rim.SetParent(o.body, false);
        rim.localScale = new Vector3(1f + 2f * RimWidth / size.x, 0.5f, 1f + 2f * RimWidth / size.y);
        rim.localPosition = new Vector3(0f, round ? -1.5f : -0.75f, 0f);

        if (item.kind == (int)Kind.Note)
        {
            if (item.text == null) item.text = "";
            if (item.strokes == null) item.strokes = new List<NoteStroke>();

            o.content = new GameObject("NoteContent").transform;
            o.content.SetParent(root, false);

            if (font != null)
            {
                GameObject textObject = new GameObject("Text");
                textObject.transform.SetParent(o.content, false);
                float edge = NoteSize / 2f - NotePadding;
                textObject.transform.localPosition = new Vector3(-edge, Thickness / 2f + 0.001f, edge);
                textObject.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

                o.label = textObject.AddComponent<TextMesh>();
                o.label.font = font;
                o.label.fontSize = 48;
                o.label.characterSize = 0.0029f;
                o.label.anchor = TextAnchor.UpperLeft;
                o.label.alignment = TextAlignment.Left;
                o.label.color = NoteInk;
                textObject.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            }
            RebuildContent(o);
        }

        objects.Add(o);
        Raise(o);
        return o;
    }

    void RebuildContent(Obj o)
    {
        if (o.content == null) return;
        if (o.label != null) o.label.text = Wrap(o.item.text);

        foreach (GameObject line in o.lines) if (line != null) Destroy(line);
        o.lines.Clear();

        foreach (NoteStroke s in o.item.strokes)
        {
            if (s == null || s.pts == null || s.pts.Count < 2) continue;
            GameObject go = new GameObject("NoteStroke");
            go.transform.SetParent(o.content, false);
            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.sharedMaterial = board.lineMaterial;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            block.SetColor("_BaseColor", NoteInk);
            block.SetColor("_Color", NoteInk);
            lr.SetPropertyBlock(block);
            lr.widthMultiplier = 0.0035f;
            lr.numCapVertices = 3;
            lr.numCornerVertices = 3;
            lr.positionCount = s.pts.Count;
            for (int i = 0; i < s.pts.Count; i++)
            {
                lr.SetPosition(i, new Vector3((s.pts[i].x - 0.5f) * NoteSize,
                                              Thickness / 2f + 0.0015f,
                                              (s.pts[i].y - 0.5f) * NoteSize));
            }
            o.lines.Add(go);
        }
    }

    void DestroyObj(Obj o)
    {
        if (o.body != null) Destroy(o.body.gameObject);
        if (o.content != null) Destroy(o.content.gameObject);
    }


    void Save()
    {
        SaveData data = new SaveData();
        foreach (Obj o in objects) data.items.Add(o.item);
        try { File.WriteAllText(SavePath, JsonUtility.ToJson(data)); }
        catch (Exception e) { Debug.LogWarning("Object save failed: " + e.Message); }
    }

    void Load()
    {
        SaveData data = null;
        if (File.Exists(SavePath))
        {
            try { data = JsonUtility.FromJson<SaveData>(File.ReadAllText(SavePath)); }
            catch (Exception e) { Debug.LogWarning("Object load failed: " + e.Message); }
        }

        if (data != null && data.items != null)
        {
            foreach (Item item in data.items)
            {
                if (item.kind < 0 || item.kind >= Sizes.Length) continue;
                if (item.home) continue;
                Build(item);
            }
        }
        Refresh();
    }
}