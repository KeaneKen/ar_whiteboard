using System.Collections;
using UnityEngine;

public class BoardFeedback : MonoBehaviour
{
    [Header("Feedback")]
    public bool soundOn = true;
    public bool hapticsOn = true;
    public bool effectsOn = true;
    [Range(0f, 1f)] public float volume = 0.8f;

    const int SampleRate = 44100;
    const float TickInterval = 0.04f;

    DrawingBoard board;
    AudioSource source;
    AudioClip popClip, dropClip, poofClip, denyClip;
    MaterialPropertyBlock block;
    float nextTick;

#if UNITY_ANDROID && !UNITY_EDITOR
    AndroidJavaObject vibrator;
    AndroidJavaClass effectClass;
#endif

    void Awake()
    {
        board = GetComponent<DrawingBoard>();
        block = new MaterialPropertyBlock();

        source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 0f;

        popClip = MakeSweep("Pop", 0.07f, 950f, 380f, 38f);
        dropClip = MakeSweep("Drop", 0.11f, 420f, 170f, 26f);
        denyClip = MakeSweep("Deny", 0.14f, 190f, 150f, 14f);
        poofClip = MakeNoise("Poof", 0.45f);

        if (FindAnyObjectByType<AudioListener>() == null)
        {
            Camera cam = board != null && board.arCamera != null ? board.arCamera : Camera.main;
            if (cam != null) cam.gameObject.AddComponent<AudioListener>();
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                vibrator = activity.Call<AndroidJavaObject>("getSystemService", "vibrator");
            }
            effectClass = new AndroidJavaClass("android.os.VibrationEffect");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("Vibration unavailable: " + e.Message);
            vibrator = null;
        }
#endif
    }

    static void KeepVibratePermission()
    {
        Handheld.Vibrate();
    }

    public void Click()
    {
        Play(popClip, 0.7f);
        Vibrate(18, 110);
    }

    public void Grab()
    {
        Play(popClip, 0.5f);
        Vibrate(25, 140);
    }

    public void Drop()
    {
        Play(dropClip, 0.9f);
        Vibrate(30, 170);
    }

    public void Deny()
    {
        Play(denyClip, 0.9f);
        Vibrate(70, 200);
    }

    public void DrawTick()
    {
        if (Time.unscaledTime < nextTick) return;
        nextTick = Time.unscaledTime + TickInterval;
        Vibrate(14, 55);
    }

    public void Poof()
    {
        Play(poofClip, 1f);
        Vibrate(45, 120);
        if (!effectsOn || board == null || board.lineMaterial == null) return;

        Transform plane = board.transform;
        Transform parent = plane.parent != null ? plane.parent : plane;
        float w = Mathf.Abs(plane.lossyScale.x) * 10f;
        float h = Mathf.Abs(plane.lossyScale.z) * 10f;
        Vector3 center = plane.position;

        const int edgePuffs = 22;
        for (int i = 0; i < edgePuffs; i++)
        {
            float t = (i + Random.value * 0.6f) / edgePuffs * 4f;
            Vector2 p;
            if (t < 1f) p = new Vector2(t - 0.5f, -0.5f);
            else if (t < 2f) p = new Vector2(0.5f, t - 1.5f);
            else if (t < 3f) p = new Vector2(2.5f - t, 0.5f);
            else p = new Vector2(-0.5f, 3.5f - t);

            Vector3 start = center + plane.right * (p.x * w) + plane.forward * (p.y * h);
            Vector3 outward = (start - center).normalized;
            StartCoroutine(Puff(parent, plane, start, outward, Random.Range(0.14f, 0.26f), Random.Range(0f, 0.12f)));
        }

        const int markerPuffs = 9;
        for (int i = 0; i < markerPuffs; i++)
        {
            float angle = (i + Random.value * 0.5f) / markerPuffs * Mathf.PI * 2f;
            Vector3 outward = plane.right * Mathf.Cos(angle) + plane.forward * Mathf.Sin(angle);
            Vector3 start = parent.position + outward * 0.12f;
            StartCoroutine(Puff(parent, plane, start, outward, Random.Range(0.07f, 0.13f), Random.Range(0f, 0.08f)));
        }
    }

    IEnumerator Puff(Transform parent, Transform plane, Vector3 start, Vector3 outward, float size, float delay)
    {
        if (delay > 0f) yield return new WaitForSeconds(delay);

        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Collider col = go.GetComponent<Collider>();
        if (col != null) Destroy(col);
        go.name = "Puff";
        go.transform.SetParent(parent, true);

        Renderer r = go.GetComponent<Renderer>();
        r.sharedMaterial = board.lineMaterial;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        float shade = Random.Range(0.86f, 1f);
        Color c = new Color(shade, shade, shade);
        block.SetColor("_BaseColor", c);
        block.SetColor("_Color", c);
        r.SetPropertyBlock(block);

        Vector3 drift = outward * (size * 1.4f) + plane.up * (size * 0.6f);
        float parentScale = Mathf.Max(Mathf.Abs(parent.lossyScale.x), 1e-6f);
        const float duration = 0.65f;

        for (float t = 0f; t < duration; t += Time.deltaTime)
        {
            float k = t / duration;
            float ease = 1f - (1f - k) * (1f - k);
            float scale = k < 0.25f ? k / 0.25f : 1f - (k - 0.25f) / 0.75f;
            go.transform.position = start + plane.up * 0.02f + drift * ease;
            go.transform.localScale = Vector3.one * (size * Mathf.Max(scale, 0.001f) / parentScale);
            yield return null;
        }
        Destroy(go);
    }

    void Play(AudioClip clip, float gain)
    {
        if (!soundOn || clip == null || source == null) return;
        source.PlayOneShot(clip, volume * gain);
    }

    void Vibrate(int milliseconds, int amplitude)
    {
        if (!hapticsOn) return;
#if UNITY_ANDROID && !UNITY_EDITOR
        if (vibrator == null) return;
        try
        {
            using (AndroidJavaObject effect = effectClass.CallStatic<AndroidJavaObject>(
                    "createOneShot", (long)milliseconds, Mathf.Clamp(amplitude, 1, 255)))
            {
                vibrator.Call("vibrate", effect);
            }
        }
        catch (System.Exception)
        {
            vibrator = null;
        }
#endif
    }

    static AudioClip MakeSweep(string name, float seconds, float fromHz, float toHz, float decay)
    {
        int n = Mathf.CeilToInt(seconds * SampleRate);
        float[] data = new float[n];
        float phase = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            float k = i / (float)n;
            float hz = Mathf.Lerp(fromHz, toHz, k);
            phase += 2f * Mathf.PI * hz / SampleRate;
            float attack = Mathf.Clamp01(t / 0.004f);
            float release = Mathf.Clamp01((seconds - t) / 0.01f);
            data[i] = Mathf.Sin(phase) * Mathf.Exp(-decay * t) * attack * release * 0.8f;
        }
        AudioClip clip = AudioClip.Create(name, n, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    static AudioClip MakeNoise(string name, float seconds)
    {
        int n = Mathf.CeilToInt(seconds * SampleRate);
        float[] data = new float[n];
        System.Random random = new System.Random(7);
        float low = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            float k = i / (float)n;
            float white = (float)(random.NextDouble() * 2.0 - 1.0);
            float cutoff = Mathf.Lerp(0.22f, 0.03f, k);
            low += (white - low) * cutoff;
            float attack = Mathf.Clamp01(t / 0.02f);
            data[i] = low * attack * Mathf.Exp(-7f * t) * 1.6f;
        }
        AudioClip clip = AudioClip.Create(name, n, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }
}