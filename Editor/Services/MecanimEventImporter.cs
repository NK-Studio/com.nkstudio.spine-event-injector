using System;
using System.Collections.Generic;
using System.Linq;
using Spine.Unity;
using UnityEditor;
using UnityEngine;

namespace NKStudio.SpineEventInjector
{
    /// <summary>
    /// Unity AnimationEvent 이식: spine-unity 가 스켈레톤에서 만든 Mecanim 클립에 사람이 직접 단 이벤트를 찾아
    /// 작업본의 Spine 이벤트(정의 + 키)로 옮긴다. spine-unity 가 만든 이벤트(stringParameter == "SpineEvent")는 건너뛴다.
    /// Spine 이벤트 이름을 함수 이름과 같게 지으므로 받는 쪽 코드는 그대로 동작한다.
    /// </summary>
    static class MecanimEventImporter
    {
        const string SpineEventStringId = "SpineEvent";

        public sealed class Found
        {
            public string Animation;
            public float Time;
            public string Function;
            public int Int;
            public float Float;
            public string String;
            public bool HasObjectParameter;
        }

        public static List<Found> Scan(SkeletonDataAsset asset)
        {
            var found = new List<Found>();
            if (asset == null || asset.controller == null)
            {
                return found;
            }

            foreach (AnimationClip clip in asset.controller.animationClips.Distinct())
            {
                foreach (AnimationEvent e in AnimationUtility.GetAnimationEvents(clip))
                {
                    if (e.stringParameter == SpineEventStringId)
                    {
                        continue;
                    }

                    found.Add(new Found
                    {
                        Animation = clip.name,
                        Time = e.time,
                        Function = e.functionName,
                        Int = e.intParameter,
                        Float = e.floatParameter,
                        String = e.stringParameter ?? string.Empty,
                        HasObjectParameter = e.objectReferenceParameter != null,
                    });
                }
            }

            return found;
        }

        /// <summary>작업본에 아직 없는(같은 애니메이션·이름·시각의 키가 없는) 것만.</summary>
        public static List<Found> Pending(IEnumerable<Found> found, EventSet set) =>
            found.Where(f => set.Animations.Contains(f.Animation) &&
                             set.Keys.Any(k => k.Animation == f.Animation && k.Name == f.Function && Math.Abs(k.Seconds - f.Time) < 1e-3) == false).ToList();

        /// <summary>작업본에 옮긴다(같은 애니메이션·이름·시각의 키가 이미 있으면 건너뛴다). (넣은 키 수, 건너뛴 설명들)</summary>
        public static (int Added, List<string> Skipped) Import(EventSource source, IEnumerable<Found> found)
        {
            int added = 0;
            var skipped = new List<string>();
            source.Edit(set =>
            {
                foreach (Found f in found)
                {
                    if (set.Animations.Contains(f.Animation) == false)
                    {
                        skipped.Add(Loc.T("{0}/{1}: 스켈레톤에 같은 이름의 애니메이션이 없음", f.Animation, f.Function));
                        continue;
                    }

                    if (set.Keys.Any(k => k.Animation == f.Animation && k.Name == f.Function && Math.Abs(k.Seconds - f.Time) < 1e-3))
                    {
                        skipped.Add(Loc.T("{0} {1:0.###}초 {2}: 이미 있음", f.Animation, f.Time, f.Function));
                        continue;
                    }

                    if (f.HasObjectParameter)
                    {
                        skipped.Add(Loc.T("{0}/{1}: 오브젝트 인자는 Spine 이벤트로 옮길 수 없어 값 없이 옮김", f.Animation, f.Function));
                    }

                    if (set.Definition(f.Function) == null)
                    {
                        set.Definitions.Add(new EventDefinition { Name = f.Function });
                    }

                    set.Keys.Add(new EventKey
                    {
                        Animation = f.Animation,
                        Seconds = f.Time,
                        Name = f.Function,
                        Int = f.Int,
                        Float = f.Float,
                        String = f.String,
                    });
                    added++;
                }
            });
            return (added, skipped);
        }
    }
}
