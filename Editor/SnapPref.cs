using UnityEditor;

namespace NKStudio.SpineEventInjector
{
    /// <summary>재생 헤드를 끌 때 어디에 붙을지.</summary>
    enum ScrubSnap
    {
        Free,
        Frame,
        Key,
    }

    /// <summary>키를 끌 때 어디에 붙을지.</summary>
    enum KeySnap
    {
        Free,
        Frame,
        Playhead,
    }

    /// <summary>도구 막대의 스냅 모드와 재생 속도(EditorPrefs).</summary>
    static class SnapPref
    {
        const string ScrubKey = "NKStudio.SpineEventInjector.ScrubSnap";
        const string KeyKey = "NKStudio.SpineEventInjector.KeySnap";
        const string SpeedKey = "NKStudio.SpineEventInjector.Speed";

        public static ScrubSnap Scrub
        {
            get => (ScrubSnap)EditorPrefs.GetInt(ScrubKey, (int)ScrubSnap.Frame);
            set => EditorPrefs.SetInt(ScrubKey, (int)value);
        }

        public static KeySnap Key
        {
            get => (KeySnap)EditorPrefs.GetInt(KeyKey, (int)KeySnap.Frame);
            set => EditorPrefs.SetInt(KeyKey, (int)value);
        }

        public static float PlaybackSpeed
        {
            get => EditorPrefs.GetFloat(SpeedKey, 1f);
            set => EditorPrefs.SetFloat(SpeedKey, value);
        }
    }
}
