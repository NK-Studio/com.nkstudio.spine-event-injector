using System.Collections.Generic;

namespace NKStudio.SpineEventInjector
{
    /// <summary>
    /// 화면 언어(영어 기본, 한국어 선택). 원문은 한국어로 쓰고, 영어일 때 <see cref="LocTable"/> 에서 바꾼다.
    /// 표에 없는 문장은 원문 그대로 보인다.
    /// </summary>
    public static class Loc
    {
        public static bool Korean;
        /// <summary>맥이면 수정 키 이름을 «Option»으로 보인다(윈도우는 «Alt»). 에디터가 켜질 때 정한다.</summary>
        public static bool Mac;

        public static string T(string korean) => Keys(Korean || LocTable.En.TryGetValue(korean, out string en) == false ? korean : en);

        static string Keys(string s) => Mac && s != null ? s.Replace("(Alt:", "(Option:").Replace("Alt 를", "Option 을") : s;

        public static string T(string korean, params object[] args) => string.Format(T(korean), args);

        /// <summary>표에 있으면 바꾼 문장, 없으면 null(UXML 일괄 번역용).</summary>
        public static string TryT(string korean)
        {
            string t = Korean ? null : LocTable.En.TryGetValue(korean, out string en) ? en : null;
            string shown = Keys(t ?? korean);
            return shown != korean ? shown : t;
        }

        public static IReadOnlyDictionary<string, string> Table => LocTable.En;
    }
}
