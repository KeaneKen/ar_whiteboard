using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

// Put this on the board Plane (needs its Mesh Collider).
public class DrawingBoard : MonoBehaviour
{
    [Serializable]
    public class StrokeData
    {
        public List<Vector2> uv = new List<Vector2>();
        public Color color = Color.red;
        public float width = 0.01f;
    }

    [Serializable]
    public class BoardData { public List<StrokeData> strokes = new List<StrokeData>(); }

    enum Popup { None, Pen, Eraser }

    [Header("Scene references")]
    public Camera arCamera;
    public Material lineMaterial;

    [Header("Toolbar icons (white PNGs)")]
    public Texture2D trashIcon;
    public Texture2D eraserIcon;

    [Header("Drawing")]
    public float lineWidth = 0.01f;     // starting pen size (meters)
    public float minPointDistance = 0.005f;
    public float surfaceOffset = 0.005f;

    [Header("Eraser")]
    public float eraseRadius = 0.02f;   // starting eraser radius (meters)

    [Header("Toolbar position (fraction of screen height from the top)")]
    [Range(0.05f, 0.95f)] public float toolbarY = 0.78f;

    const float PointSpacing = 0.005f;  // strokes are resampled to this spacing (meters) so the eraser can cut them cleanly

    static readonly Color[] Palette =
    {
        new Color(0.90f, 0.05f, 0.10f), // red
        new Color(0.10f, 0.35f, 0.95f), // blue
        new Color(0.10f, 0.70f, 0.30f), // green
        new Color(0.05f, 0.05f, 0.05f), // black
        new Color(1.00f, 0.55f, 0.05f), // orange
        new Color(0.55f, 0.25f, 0.85f), // purple
    };

    // Size options (meters) and the dot size (fraction of a cell) that represents each one.
    static readonly float[] PenSizes = { 0.005f, 0.010f, 0.020f, 0.035f };
    static readonly float[] PenDots = { 0.14f, 0.24f, 0.38f, 0.56f };
    static readonly float[] EraserSizes = { 0.010f, 0.020f, 0.040f, 0.070f };
    static readonly float[] EraserDots = { 0.18f, 0.30f, 0.46f, 0.68f };

    static readonly Color IdleBg = new Color(0.10f, 0.10f, 0.10f, 0.80f);
    static readonly Color ActiveBg = new Color(0.15f, 0.45f, 0.95f, 1.00f);
    static readonly Color DangerBg = new Color(0.90f, 0.15f, 0.15f, 1.00f);
    static readonly Color CellBg = new Color(1f, 1f, 1f, 0.14f);

    Collider boardCollider;
    Transform strokeParent;
    LineRenderer current;
    StrokeData currentData;
    readonly List<Vector3> points = new List<Vector3>();

    // strokeObjects[i] is the line for data.strokes[i]
    readonly List<GameObject> strokeObjects = new List<GameObject>();
    BoardData data = new BoardData();

    LineRenderer ring;                  // eraser cursor
    Texture2D roundTex, circleTex;

    Color inkColor = Palette[0];
    float penWidth;
    bool eraseMode;
    Popup popup = Popup.None;
    bool erasedThisTouch;
    bool pointerDown;
    Vector2 pointerGui;                 // pointer in IMGUI coordinates (y runs top-down)
    float clearFlashUntil;

    string SavePath => Path.Combine(Application.persistentDataPath, "board.json");

    // ------------------------------------------------------------ setup

    void Awake()
    {
        boardCollider = GetComponent<Collider>();
        strokeParent = transform.parent != null ? transform.parent : transform;
        if (arCamera == null) arCamera = Camera.main;
        penWidth = lineWidth;
        roundTex = MakeRounded(128, 30f);
        circleTex = MakeRounded(128, 64f);
    }

    void Start() { Load(); }

    void OnDestroy()
    {
        if (roundTex != null) Destroy(roundTex);
        if (circleTex != null) Destroy(circleTex);
    }

    // ------------------------------------------------------------ input

    void Update()
    {
        pointerDown = TryGetPointer(out Vector2 screenPos);
        pointerGui = new Vector2(screenPos.x, Screen.height - screenPos.y);

        if (!pointerDown)
        {
            EndStroke();
            HideRing();
            if (erasedThisTouch) { Save(); erasedThisTouch = false; }
            return;
        }

        if (OverUI(screenPos)) { HideRing(); return; }

        // Touching the board closes any open pop-up.
        popup = Popup.None;

        Ray ray = arCamera.ScreenPointToRay(screenPos);
        bool hitBoard = boardCollider.Raycast(ray, out RaycastHit hit, 100f);

        if (eraseMode)
        {
            if (hitBoard) { ShowRing(hit.point); EraseAt(hit.point); }
            else HideRing();
            return;
        }

        HideRing();
        if (hitBoard)
        {
            if (current == null) StartStroke();
            AddPoint(hit.point);
        }
        else
        {
            EndStroke(); // finger slid off the board
        }
    }

    bool TryGetPointer(out Vector2 pos)
    {
        pos = default;
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

    // ------------------------------------------------------------ board coordinates

    // The Plane mesh is 10 x 10 local units, so these work at any board scale.
    Vector2 WorldToUV(Vector3 world)
    {
        Vector3 l = transform.InverseTransformPoint(world);
        return new Vector2((l.x + 5f) / 10f, (l.z + 5f) / 10f);
    }

    Vector3 UVToWorld(Vector2 uv)
    {
        Vector3 l = new Vector3(uv.x * 10f - 5f, 0f, uv.y * 10f - 5f);
        return transform.TransformPoint(l) + transform.up * surfaceOffset;
    }

    float BoardW => Mathf.Abs(transform.lossyScale.x) * 10f; // meters
    float BoardH => Mathf.Abs(transform.lossyScale.z) * 10f; // meters

    // ------------------------------------------------------------ strokes

    LineRenderer CreateLine(Color c, float width)
    {
        var go = new GameObject("Stroke");
        go.transform.SetParent(strokeParent, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = false;
        lr.sharedMaterial = lineMaterial;

        var block = new MaterialPropertyBlock();
        block.SetColor("_BaseColor", c);
        block.SetColor("_Color", c);
        lr.SetPropertyBlock(block);

        lr.widthMultiplier = width;
        lr.numCapVertices = 4;
        lr.numCornerVertices = 4;
        lr.positionCount = 0;
        return lr;
    }

    void SetLine(LineRenderer lr, List<Vector2> uv)
    {
        lr.positionCount = uv.Count;
        for (int i = 0; i < uv.Count; i++)
            lr.SetPosition(i, strokeParent.InverseTransformPoint(UVToWorld(uv[i])));
    }

    GameObject BuildStroke(StrokeData s)
    {
        var lr = CreateLine(s.color, s.width);
        SetLine(lr, s.uv);
        return lr.gameObject;
    }

    // Adds points along long segments so strokes have an even point spacing.
    List<Vector2> Densify(List<Vector2> src)
    {
        float wM = BoardW, hM = BoardH;
        var result = new List<Vector2>(src.Count);
        for (int i = 0; i < src.Count; i++)
        {
            if (i > 0)
            {
                Vector2 a = src[i - 1], b = src[i];
                float len = new Vector2((b.x - a.x) * wM, (b.y - a.y) * hM).magnitude;
                int n = Mathf.CeilToInt(len / PointSpacing);
                for (int k = 1; k < n; k++) result.Add(Vector2.Lerp(a, b, k / (float)n));
            }
            result.Add(src[i]);
        }
        return result;
    }

    void StartStroke()
    {
        current = CreateLine(inkColor, penWidth);
        currentData = new StrokeData { color = inkColor, width = penWidth };
        points.Clear();
    }

    void AddPoint(Vector3 worldHit)
    {
        Vector3 p = strokeParent.InverseTransformPoint(worldHit + transform.up * surfaceOffset);
        if (points.Count > 0 && Vector3.Distance(points[points.Count - 1], p) < minPointDistance) return;
        points.Add(p);
        currentData.uv.Add(WorldToUV(worldHit));
        current.positionCount = points.Count;
        current.SetPosition(points.Count - 1, p);
    }

    void EndStroke()
    {
        if (current == null) return;
        if (currentData.uv.Count >= 2)
        {
            currentData.uv = Densify(currentData.uv);
            SetLine(current, currentData.uv);
            data.strokes.Add(currentData);
            strokeObjects.Add(current.gameObject);
            Save();
        }
        else
        {
            Destroy(current.gameObject); // a tap with no movement
        }
        current = null;
        currentData = null;
        points.Clear();
    }

    // ------------------------------------------------------------ eraser (cuts strokes under the circle)

    void EraseAt(Vector3 worldHit)
    {
        float wM = BoardW, hM = BoardH;
        Vector2 uv0 = WorldToUV(worldHit);
        Vector2 c = new Vector2(uv0.x * wM, uv0.y * hM);
        float r2 = eraseRadius * eraseRadius;

        for (int i = data.strokes.Count - 1; i >= 0; i--)
        {
            StrokeData s = data.strokes[i];

            bool touched = false;
            for (int k = 0; k < s.uv.Count; k++)
            {
                Vector2 m = new Vector2(s.uv[k].x * wM, s.uv[k].y * hM);
                if ((m - c).sqrMagnitude < r2) { touched = true; break; }
            }
            if (!touched) continue;

            // Keep the runs of points outside the circle; each run becomes its own stroke.
            var pieces = new List<List<Vector2>>();
            var run = new List<Vector2>();
            foreach (Vector2 uv in s.uv)
            {
                Vector2 m = new Vector2(uv.x * wM, uv.y * hM);
                if ((m - c).sqrMagnitude < r2)
                {
                    if (run.Count >= 2) pieces.Add(run);
                    run = new List<Vector2>();
                }
                else run.Add(uv);
            }
            if (run.Count >= 2) pieces.Add(run);

            Destroy(strokeObjects[i]);
            strokeObjects.RemoveAt(i);
            data.strokes.RemoveAt(i);

            for (int p = 0; p < pieces.Count; p++)
            {
                var piece = new StrokeData { color = s.color, width = s.width, uv = pieces[p] };
                data.strokes.Insert(i + p, piece);
                strokeObjects.Insert(i + p, BuildStroke(piece));
            }
            erasedThisTouch = true;
        }
    }

    void ShowRing(Vector3 center)
    {
        if (ring == null)
        {
            var go = new GameObject("EraserRing");
            go.transform.SetParent(strokeParent, false);
            ring = go.AddComponent<LineRenderer>();
            ring.useWorldSpace = true;
            ring.loop = true;
            ring.sharedMaterial = lineMaterial;
            var block = new MaterialPropertyBlock();
            Color grey = new Color(0.25f, 0.25f, 0.25f);
            block.SetColor("_BaseColor", grey);
            block.SetColor("_Color", grey);
            ring.SetPropertyBlock(block);
            ring.widthMultiplier = 0.003f;
        }

        const int n = 36;
        ring.positionCount = n;
        Vector3 lift = transform.up * (surfaceOffset * 2f);
        for (int k = 0; k < n; k++)
        {
            float a = k * Mathf.PI * 2f / n;
            ring.SetPosition(k, center + lift
                + transform.right * (Mathf.Cos(a) * eraseRadius)
                + transform.forward * (Mathf.Sin(a) * eraseRadius));
        }
    }

    void HideRing()
    {
        if (ring != null) ring.positionCount = 0;
    }

    void SetEraseMode(bool on)
    {
        EndStroke();
        eraseMode = on;
        popup = Popup.None;
    }

    // ------------------------------------------------------------ save / load / clear

    void Save()
    {
        try { File.WriteAllText(SavePath, JsonUtility.ToJson(data)); }
        catch (Exception e) { Debug.LogWarning("Save failed: " + e.Message); }
    }

    void Load()
    {
        if (!File.Exists(SavePath)) return;
        try { data = JsonUtility.FromJson<BoardData>(File.ReadAllText(SavePath)) ?? new BoardData(); }
        catch (Exception e)
        {
            Debug.LogWarning("Load failed: " + e.Message);
            data = new BoardData();
            return;
        }

        foreach (StrokeData s in data.strokes)
        {
            if (s.color.a <= 0f) s.color = Palette[0]; // strokes saved before colors existed
            if (s.width <= 0f) s.width = lineWidth;    // strokes saved before sizes existed
            s.uv = Densify(s.uv);
            strokeObjects.Add(BuildStroke(s));
        }
    }

    public void Clear()
    {
        EndStroke();
        foreach (GameObject go in strokeObjects) if (go != null) Destroy(go);
        strokeObjects.Clear();
        data = new BoardData();
        Save();
    }

    // ------------------------------------------------------------ toolbar layout

    float BtnSize => Screen.height * 0.08f;
    float Gap => BtnSize * 0.25f;
    float BtnGap => BtnSize * 0.45f;

    // index: 0 = pen/color, 1 = eraser, 2 = trash
    Rect Btn(int index)
    {
        float s = BtnSize, g = BtnGap;
        float total = 3f * s + 2f * g;
        float left = (Screen.width - total) / 2f;
        return new Rect(left + index * (s + g), Screen.height * toolbarY - s / 2f, s, s);
    }

    // Pop-up cells: sized so a row of 6 always fits on screen.
    float CellSize => Mathf.Min(BtnSize * 0.8f, Screen.width * 0.86f / 7f);
    float CellGap => CellSize * 0.3f;

    // row 0 sits just above the toolbar; higher rows stack upward.
    Rect Item(int row, int j, int count)
    {
        float s = CellSize, g = CellGap;
        float total = count * s + (count - 1) * g;
        float left = (Screen.width - total) / 2f;
        float bottomRowY = Btn(0).y - Gap * 1.4f - s;
        return new Rect(left + j * (s + g), bottomRowY - row * (s + g), s, s);
    }

    // Pen pop-up: row 0 = colors, row 1 = sizes. Eraser pop-up: row 0 = sizes.
    int PopupRows => popup == Popup.Pen ? 2 : 1;
    int PopupWidest => popup == Popup.Pen ? Palette.Length : EraserSizes.Length;

    Rect PopupTray()
    {
        int n = PopupWidest;
        Rect bl = Item(0, 0, n), br = Item(0, n - 1, n), tl = Item(PopupRows - 1, 0, n);
        return Grow(Rect.MinMaxRect(bl.xMin, tl.yMin, br.xMax, bl.yMax), CellGap);
    }

    static Rect Grow(Rect r, float by)
    {
        return Rect.MinMaxRect(r.xMin - by, r.yMin - by, r.xMax + by, r.yMax + by);
    }

    static Rect Inset(Rect r, float fraction)
    {
        return new Rect(r.x + r.width * fraction, r.y + r.height * fraction,
                        r.width * (1f - 2f * fraction), r.height * (1f - 2f * fraction));
    }

    bool OverUI(Vector2 screenPos)
    {
        Vector2 p = new Vector2(screenPos.x, Screen.height - screenPos.y);
        Rect bar = Rect.MinMaxRect(Btn(0).xMin, Btn(0).yMin, Btn(2).xMax, Btn(2).yMax);
        if (Grow(bar, Gap).Contains(p)) return true;
        if (popup != Popup.None && Grow(PopupTray(), CellGap * 0.5f).Contains(p)) return true;
        return false;
    }

    // ------------------------------------------------------------ toolbar drawing

    // GUI colors are treated as linear in a Linear color-space project, which washes them out.
    static Color Lin(Color c)
    {
        return QualitySettings.activeColorSpace == ColorSpace.Linear ? c.linear : c;
    }

    // Rounded rectangle with true circular corners at any size (9-slice of the rounded texture).
    // 'corner' is the corner radius in pixels; by default a quarter of the shorter side.
    void DrawRound(Rect r, Color c, float corner = -1f)
    {
        float cs = corner > 0f ? corner : Mathf.Min(r.width, r.height) * 0.24f;
        cs = Mathf.Min(cs, Mathf.Min(r.width, r.height) / 2f);

        // Snap to whole pixels so the 9 slices meet exactly (no hairline seams).
        float x0 = Mathf.Round(r.xMin), x3 = Mathf.Round(r.xMax);
        float y0 = Mathf.Round(r.yMin), y3 = Mathf.Round(r.yMax);
        cs = Mathf.Round(cs);
        float x1 = x0 + cs, x2 = x3 - cs, y1 = y0 + cs, y2 = y3 - cs;
        const float q = 0.25f; // corner size inside the 128px texture (radius 30px fits in 32px)

        Color old = GUI.color;
        GUI.color = Lin(c);
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                float dx0 = col == 0 ? x0 : col == 1 ? x1 : x2;
                float dx1 = col == 0 ? x1 : col == 1 ? x2 : x3;
                float dy0 = row == 0 ? y0 : row == 1 ? y1 : y2;
                float dy1 = row == 0 ? y1 : row == 1 ? y2 : y3;
                if (dx1 - dx0 <= 0f || dy1 - dy0 <= 0f) continue;

                float u0 = col == 0 ? 0f : col == 1 ? q : 1f - q;
                float u1 = col == 0 ? q : col == 1 ? 1f - q : 1f;
                float v1 = row == 0 ? 1f : row == 1 ? 1f - q : q;   // texture v runs bottom-up
                float v0 = row == 0 ? 1f - q : row == 1 ? q : 0f;

                GUI.DrawTextureWithTexCoords(Rect.MinMaxRect(dx0, dy0, dx1, dy1), roundTex,
                                             Rect.MinMaxRect(u0, v0, u1, v1));
            }
        }
        GUI.color = old;
    }

    void DrawCircle(Rect r, Color c)
    {
        Color old = GUI.color;
        GUI.color = Lin(c);
        GUI.DrawTexture(r, circleTex);
        GUI.color = old;
    }

    void DrawIconButton(Rect r, Texture2D icon, Color bg)
    {
        DrawRound(r, bg);
        if (icon == null) return;
        Color old = GUI.color;
        GUI.color = Color.white;
        GUI.DrawTexture(Inset(r, 0.18f), icon, ScaleMode.ScaleToFit, true);
        GUI.color = old;
    }

    // One row of size options; returns the index tapped, or -1.
    int DrawSizeRow(int row, float[] sizes, float[] dots, float selectedSize)
    {
        int tapped = -1;
        int n = sizes.Length, widest = PopupWidest;
        Rect first = Item(row, 0, widest), last = Item(row, widest - 1, widest);
        float full = last.xMax - first.xMin;
        float cw = (full - (n - 1) * CellGap) / n;   // stretch cells to fill the tray width
        for (int i = 0; i < n; i++)
        {
            Rect cell = new Rect(first.xMin + i * (cw + CellGap), first.y, cw, CellSize);
            bool selected = Mathf.Approximately(sizes[i], selectedSize);
            DrawRound(cell, selected ? ActiveBg : CellBg, CellSize * 0.25f);
            float d = CellSize * dots[i];
            DrawCircle(new Rect(cell.center.x - d / 2f, cell.center.y - d / 2f, d, d), Color.white);
            if (GUI.Button(cell, GUIContent.none, GUIStyle.none)) tapped = i;
        }
        return tapped;
    }

    void OnGUI()
    {
        if (roundTex == null) return;

        // Pen: selected when not erasing. Shows the ink color. First tap selects it and opens
        // the color + size pop-up; tapping it again closes the pop-up.
        Rect pen = Btn(0);
        DrawRound(pen, eraseMode ? IdleBg : ActiveBg);
        DrawCircle(Inset(pen, 0.19f), Color.white);
        DrawCircle(Inset(pen, 0.26f), inkColor);
        if (GUI.Button(pen, GUIContent.none, GUIStyle.none))
        {
            if (eraseMode) { SetEraseMode(false); popup = Popup.Pen; }
            else popup = popup == Popup.Pen ? Popup.None : Popup.Pen;
        }

        // Eraser: selecting it deselects the pen and opens its size pop-up.
        Rect er = Btn(1);
        DrawIconButton(er, eraserIcon, eraseMode ? ActiveBg : IdleBg);
        if (GUI.Button(er, GUIContent.none, GUIStyle.none))
        {
            if (!eraseMode) { SetEraseMode(true); popup = Popup.Eraser; }
            else popup = popup == Popup.Eraser ? Popup.None : Popup.Eraser;
        }

        // Trash: a one-shot button. Red while pressed and for a moment after, never stays selected.
        Rect tr = Btn(2);
        bool pressing = pointerDown && tr.Contains(pointerGui);
        bool flash = Time.unscaledTime < clearFlashUntil;
        DrawIconButton(tr, trashIcon, (pressing || flash) ? DangerBg : IdleBg);
        if (GUI.Button(tr, GUIContent.none, GUIStyle.none))
        {
            Clear();
            popup = Popup.None;
            clearFlashUntil = Time.unscaledTime + 0.3f;
        }

        if (popup == Popup.None) return;

        DrawRound(PopupTray(), IdleBg, CellSize * 0.35f);

        if (popup == Popup.Pen)
        {
            // Row 0: colors
            for (int i = 0; i < Palette.Length; i++)
            {
                Rect sr = Item(0, i, Palette.Length);
                bool selected = Palette[i] == inkColor;
                if (selected) DrawCircle(sr, Color.white);
                DrawCircle(Inset(sr, selected ? 0.14f : 0.06f), Palette[i]);
                if (GUI.Button(sr, GUIContent.none, GUIStyle.none)) inkColor = Palette[i];
            }
            // Row 1: pen sizes
            int t = DrawSizeRow(1, PenSizes, PenDots, penWidth);
            if (t >= 0) penWidth = PenSizes[t];
        }
        else
        {
            int t = DrawSizeRow(0, EraserSizes, EraserDots, eraseRadius);
            if (t >= 0) eraseRadius = EraserSizes[t];
        }
    }

    // White rounded-rectangle texture with antialiased edges (radius = size / 2 gives a circle).
    static Texture2D MakeRounded(int size, float radius)
    {
        var t = new Texture2D(size, size, TextureFormat.RGBA32, false);
        t.wrapMode = TextureWrapMode.Clamp;
        t.filterMode = FilterMode.Bilinear;
        var px = new Color32[size * size];
        float half = size / 2f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = Mathf.Abs(x + 0.5f - half) - (half - radius);
                float dy = Mathf.Abs(y + 0.5f - half) - (half - radius);
                float d = new Vector2(Mathf.Max(dx, 0f), Mathf.Max(dy, 0f)).magnitude
                          + Mathf.Min(Mathf.Max(dx, dy), 0f) - radius;
                float a = Mathf.Clamp01(0.5f - d);
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        }
        t.SetPixels32(px);
        t.Apply();
        return t;
    }
}