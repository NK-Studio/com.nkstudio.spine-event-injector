using System;
using System.Collections.Generic;

namespace NKStudio.SpineEventInjector.SpineVersionConverter
{
    /// <summary>
    /// Spine 3.x ⇄ 4.x 경계를 넘을 때 모델에 거는 변환 패스.
    /// 원본: Spine-Custom-Editor src/CurveConverter.cpp · RotateTimelineConverter.cpp · SpacingModeConverter.cpp · OrderConverter.cpp.
    /// - 3.x 곡선은 키 구간을 0~1 로 정규화한 베지어 한 개(cx1, cy1, cx2, cy2)를 모든 성분이 나눠 쓴다.
    ///   4.x 곡선은 성분마다 (시간, 값) 절대 좌표 베지어 4개씩이다.
    /// - 3.x 회전 키는 인접 키 사이를 항상 최단 경로(±180° 이내)로 돈다. 4.x 는 절대 각도라 한 구간에 360° 이상도 돈다.
    /// 부동소수 연산은 원본과 같은 정밀도(float/double)·순서로 적었다 — 바이트 단위로 같은 결과를 내기 위해서다.
    /// </summary>
    public static class Spine3xConversions
    {
        private enum CurveComponent
        {
            R1,
            G1,
            B1,
            A1,
            R2,
            G2,
            B2,
            V1,
            V2,
            V3,
            V4,
            V5,
            V6,
            ZeroOne,
        }

        // 4.x 곡선 배열에서 이 성분이 차지하는 베지어 칸(4개 값 단위).
        private static readonly int[] BezierIndexByComponent = { 0, 1, 2, 3, 4, 5, 6, 0, 1, 2, 3, 4, 5, 0 };

        private static readonly CurveComponent[] Rgba = { CurveComponent.R1, CurveComponent.G1, CurveComponent.B1, CurveComponent.A1 };
        private static readonly CurveComponent[] Rgb = { CurveComponent.R1, CurveComponent.G1, CurveComponent.B1 };
        private static readonly CurveComponent[] Rgba2 = { CurveComponent.R1, CurveComponent.G1, CurveComponent.B1, CurveComponent.A1, CurveComponent.R2, CurveComponent.G2, CurveComponent.B2 };
        private static readonly CurveComponent[] Rgb2 = { CurveComponent.R1, CurveComponent.G1, CurveComponent.B1, CurveComponent.R2, CurveComponent.G2, CurveComponent.B2 };
        private static readonly CurveComponent[] OneValue = { CurveComponent.V1 };
        private static readonly CurveComponent[] TwoValues = { CurveComponent.V1, CurveComponent.V2 };
        private static readonly CurveComponent[] ThreeValues = { CurveComponent.V1, CurveComponent.V2, CurveComponent.V3 };
        private static readonly CurveComponent[] SixValues = { CurveComponent.V1, CurveComponent.V2, CurveComponent.V3, CurveComponent.V4, CurveComponent.V5, CurveComponent.V6 };
        private static readonly CurveComponent[] Deform = { CurveComponent.ZeroOne };

        private const float RotateEpsilon = 0.0001f;
        private const float Max3xRotateDelta = 179f;

        #region 곡선 3.x → 4.x

        public static void ConvertCurve3xTo4x(SpineSkeleton skeleton)
        {
            foreach (SpineAnimation animation in skeleton.Animations)
            {
                foreach (SpineTimelineSet timelines in animation.Slots.Values)
                {
                    foreach (KeyValuePair<string, List<SpineTimelineFrame>> entry in timelines)
                    {
                        if (entry.Key == "rgba")
                        {
                            ConvertTimelineCurve3xTo4x(entry.Value, Rgba);
                        }
                        else if (entry.Key == "rgba2")
                        {
                            ConvertTimelineCurve3xTo4x(entry.Value, Rgba2);
                        }
                    }
                }

                foreach (SpineTimelineSet timelines in animation.Bones.Values)
                {
                    foreach (KeyValuePair<string, List<SpineTimelineFrame>> entry in timelines)
                    {
                        if (entry.Key == "rotate")
                        {
                            ConvertTimelineCurve3xTo4x(entry.Value, OneValue);
                        }
                        else if (entry.Key == "translate" || entry.Key == "scale" || entry.Key == "shear")
                        {
                            ConvertTimelineCurve3xTo4x(entry.Value, TwoValues);
                        }
                    }
                }

                foreach (List<SpineTimelineFrame> timeline in animation.Ik.Values)
                {
                    ConvertTimelineCurve3xTo4x(timeline, TwoValues);
                }

                foreach (List<SpineTimelineFrame> timeline in animation.Transform.Values)
                {
                    ConvertTimelineCurve3xTo4x(timeline, SixValues);
                }

                foreach (SpineTimelineSet timelines in animation.Path.Values)
                {
                    foreach (KeyValuePair<string, List<SpineTimelineFrame>> entry in timelines)
                    {
                        if (entry.Key == "position" || entry.Key == "spacing")
                        {
                            ConvertTimelineCurve3xTo4x(entry.Value, OneValue);
                        }
                        else if (entry.Key == "mix")
                        {
                            ConvertTimelineCurve3xTo4x(entry.Value, ThreeValues);
                        }
                    }
                }

                foreach (SpineMap<SpineMap<SpineTimelineSet>> skin in animation.Attachments.Values)
                {
                    foreach (SpineMap<SpineTimelineSet> slot in skin.Values)
                    {
                        foreach (SpineTimelineSet timelines in slot.Values)
                        {
                            if (timelines.TryGetValue("deform", out List<SpineTimelineFrame> deform) == true)
                            {
                                ConvertTimelineCurve3xTo4x(deform, Deform);
                            }
                        }
                    }
                }
            }
        }

        private static void ConvertTimelineCurve3xTo4x(List<SpineTimelineFrame> timeline, CurveComponent[] components)
        {
            for (int i = 0; i + 1 < timeline.Count; i++)
            {
                SpineTimelineFrame frame = timeline[i];
                if (frame.CurveType != SpineCurveType.Bezier)
                {
                    continue;
                }

                SpineTimelineFrame next = timeline[i + 1];
                float cx1 = frame.Curve[0];
                float cy1 = frame.Curve[1];
                float cx2 = frame.Curve[2];
                float cy2 = frame.Curve[3];
                foreach (CurveComponent component in components)
                {
                    int bezierIndex = BezierIndexByComponent[(int)component];
                    while (frame.Curve.Count < (bezierIndex + 1) * 4)
                    {
                        frame.Curve.Add(0f);
                    }

                    float x1 = frame.Time;
                    float y1 = CurveValue(frame, component, false);
                    float x2 = next.Time;
                    float y2 = CurveValue(next, component, true);

                    // 정규화된 제어점을 이 구간의 (시간, 값) 절대 좌표로 펼친다.
                    float timeRange = x2 - x1;
                    float valueRange = y2 - y1;
                    frame.Curve[bezierIndex * 4 + 0] = x1 + cx1 * timeRange;
                    frame.Curve[bezierIndex * 4 + 1] = y1 + cy1 * valueRange;
                    frame.Curve[bezierIndex * 4 + 2] = x1 + cx2 * timeRange;
                    frame.Curve[bezierIndex * 4 + 3] = y1 + cy2 * valueRange;
                }
            }
        }

        #endregion

        #region 곡선 4.x → 3.x

        public static void ConvertCurve4xTo3x(SpineSkeleton skeleton)
        {
            foreach (SpineAnimation animation in skeleton.Animations)
            {
                string location = animation.Name + "/";
                foreach (KeyValuePair<string, SpineTimelineSet> slot in animation.Slots)
                {
                    foreach (KeyValuePair<string, List<SpineTimelineFrame>> entry in slot.Value)
                    {
                        string path = location + "slots/" + slot.Key + "/" + entry.Key;
                        switch (entry.Key)
                        {
                            case "rgba":
                                ConvertTimelineCurve4xTo3x(entry.Value, Rgba, path, false);
                                break;
                            case "rgb":
                                ConvertTimelineCurve4xTo3x(entry.Value, Rgb, path, false);
                                break;
                            case "rgba2":
                                ConvertTimelineCurve4xTo3x(entry.Value, Rgba2, path, false);
                                break;
                            case "rgb2":
                                ConvertTimelineCurve4xTo3x(entry.Value, Rgb2, path, false);
                                break;
                        }
                    }
                }

                foreach (KeyValuePair<string, SpineTimelineSet> bone in animation.Bones)
                {
                    foreach (KeyValuePair<string, List<SpineTimelineFrame>> entry in bone.Value)
                    {
                        string path = location + "bones/" + bone.Key + "/" + entry.Key;
                        if (entry.Key == "rotate")
                        {
                            ConvertTimelineCurve4xTo3x(entry.Value, OneValue, path, true);
                        }
                        else if (entry.Key == "translate" || entry.Key == "scale" || entry.Key == "shear")
                        {
                            ConvertTimelineCurve4xTo3x(entry.Value, TwoValues, path, false);
                        }
                    }
                }

                foreach (KeyValuePair<string, List<SpineTimelineFrame>> entry in animation.Ik)
                {
                    ConvertTimelineCurve4xTo3x(entry.Value, TwoValues, location + "ik/" + entry.Key, false);
                }

                foreach (KeyValuePair<string, List<SpineTimelineFrame>> entry in animation.Transform)
                {
                    ConvertTimelineCurve4xTo3x(entry.Value, SixValues, location + "transform/" + entry.Key, false);
                }

                foreach (KeyValuePair<string, SpineTimelineSet> pathConstraint in animation.Path)
                {
                    foreach (KeyValuePair<string, List<SpineTimelineFrame>> entry in pathConstraint.Value)
                    {
                        CurveComponent[] components = entry.Key == "mix" ? ThreeValues : OneValue;
                        ConvertTimelineCurve4xTo3x(entry.Value, components, location + "path/" + pathConstraint.Key + "/" + entry.Key, false);
                    }
                }

                foreach (KeyValuePair<string, SpineMap<SpineMap<SpineTimelineSet>>> skin in animation.Attachments)
                {
                    foreach (KeyValuePair<string, SpineMap<SpineTimelineSet>> slot in skin.Value)
                    {
                        foreach (KeyValuePair<string, SpineTimelineSet> attachment in slot.Value)
                        {
                            if (attachment.Value.TryGetValue("deform", out List<SpineTimelineFrame> deform) == true)
                            {
                                string path = location + "deform/" + skin.Key + "/" + slot.Key + "/" + attachment.Key;
                                ConvertTimelineCurve4xTo3x(deform, Deform, path, false);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 성분별 절대 베지어를 3.x 의 공유 정규화 베지어 하나로 되돌린다. 성분마다 정규화 결과가 다르면(독립 곡선)
        /// 3.x 로 표현할 수 없으므로 거부한다(원본 CURVE_NOT_PRESERVABLE).
        /// </summary>
        private static void ConvertTimelineCurve4xTo3x(List<SpineTimelineFrame> timeline, CurveComponent[] components, string location, bool isRotation)
        {
            for (int i = 0; i + 1 < timeline.Count; i++)
            {
                SpineTimelineFrame frame = timeline[i];
                SpineTimelineFrame next = timeline[i + 1];
                if (isRotation == true && frame.CurveType != SpineCurveType.Stepped &&
                    Math.Abs(next.Value1 - frame.Value1) > 180f - 0.0001f)
                {
                    throw CurveLoss(location, i, "rotation_range", "회전 구간이 180° 이상이라 3.x 최단 경로 보간으로 같은 움직임을 낼 수 없다");
                }

                if (frame.CurveType != SpineCurveType.Bezier)
                {
                    continue;
                }

                double duration = (double)next.Time - frame.Time;
                if (IsFinite(duration) == false || duration <= 0 || frame.Curve.Count != components.Length * 4)
                {
                    throw CurveLoss(location, i, "invalid_curve", "곡선 데이터가 올바르지 않다");
                }

                double[] shared = null;
                for (int component = 0; component < components.Length; component++)
                {
                    double start = CurveValue(frame, components[component], false);
                    double end = CurveValue(next, components[component], true);
                    double delta = end - start;
                    int offset = component * 4;
                    double cx1 = (frame.Curve[offset] - frame.Time) / duration;
                    double cx2 = (frame.Curve[offset + 2] - frame.Time) / duration;
                    double cy1 = frame.Curve[offset + 1];
                    double cy2 = frame.Curve[offset + 3];
                    if (IsFinite(start) == false || IsFinite(end) == false || IsFinite(cx1) == false || IsFinite(cx2) == false ||
                        IsFinite(cy1) == false || IsFinite(cy2) == false ||
                        cx1 < -0.00001 || cx1 > 1.00001 || cx2 < -0.00001 || cx2 > 1.00001)
                    {
                        throw CurveLoss(location, i, "invalid_curve", "곡선 제어점이 키 구간을 벗어난다");
                    }

                    if (delta == 0)
                    {
                        // 3.8 은 정규화 곡선 하나에 키 값 차이를 곱한다 — 양 끝 값이 같은데 움직이는 곡선은 상수가 되어 버린다.
                        if (SameControl(cy1, start) == false || SameControl(cy2, start) == false)
                        {
                            throw CurveLoss(location, i, "equal_endpoints", "양 끝 값이 같은데 중간에 움직이는 곡선은 3.x 로 표현할 수 없다");
                        }

                        continue;
                    }

                    double[] normalized = { cx1, (cy1 - start) / delta, cx2, (cy2 - start) / delta };
                    foreach (double value in normalized)
                    {
                        if (IsFinite((float)value) == false)
                        {
                            throw CurveLoss(location, i, "invalid_curve", "곡선을 정규화할 수 없다");
                        }
                    }

                    // 손잡이 위치가 달라도 같은 직선일 수 있다.
                    if (SameControl(normalized[0], normalized[1]) == true && SameControl(normalized[2], normalized[3]) == true)
                    {
                        normalized = new double[] { 0, 0, 1, 1 };
                    }

                    if (shared != null)
                    {
                        for (int c = 0; c < 4; c++)
                        {
                            if (SameControl(shared[c], normalized[c]) == false)
                            {
                                throw CurveLoss(location, i, "independent_components", "성분마다 다른 곡선은 3.x(성분 공유 곡선)로 표현할 수 없다");
                            }
                        }
                    }
                    else
                    {
                        shared = normalized;
                    }
                }

                frame.Curve.Clear();
                if (shared != null)
                {
                    foreach (double value in shared)
                    {
                        frame.Curve.Add((float)value);
                    }
                }
                else
                {
                    frame.CurveType = SpineCurveType.Linear;
                }
            }
        }

        private static SpineConversionException CurveLoss(string location, int frameIndex, string reason, string description)
        {
            return new SpineConversionException(
                $"CURVE_NOT_PRESERVABLE|{reason}|{location}|key {frameIndex + 1}: {description}. Spine 에서 곡선을 베이크하거나 키를 나눠 다시 내보낸다.");
        }

        private static bool SameControl(double a, double b)
        {
            return Math.Abs(a - b) <= 0.00001 * Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
        }

        private static bool IsFinite(double value)
        {
            return double.IsNaN(value) == false && double.IsInfinity(value) == false;
        }

        private static float CurveValue(SpineTimelineFrame frame, CurveComponent component, bool isEnd)
        {
            switch (component)
            {
                case CurveComponent.R1:
                    return RequireColor(frame.Color1).R / 255f;
                case CurveComponent.G1:
                    return RequireColor(frame.Color1).G / 255f;
                case CurveComponent.B1:
                    return RequireColor(frame.Color1).B / 255f;
                case CurveComponent.A1:
                    return RequireColor(frame.Color1).A / 255f;
                case CurveComponent.R2:
                    return RequireColor(frame.Color2).R / 255f;
                case CurveComponent.G2:
                    return RequireColor(frame.Color2).G / 255f;
                case CurveComponent.B2:
                    return RequireColor(frame.Color2).B / 255f;
                case CurveComponent.V1:
                    return frame.Value1;
                case CurveComponent.V2:
                    return frame.Value2;
                case CurveComponent.V3:
                    return frame.Value3;
                case CurveComponent.V4:
                    return frame.Value4;
                case CurveComponent.V5:
                    return frame.Value5;
                case CurveComponent.V6:
                    return frame.Value6;
                default:
                    return isEnd == true ? 1f : 0f;
            }
        }

        private static SpineColor RequireColor(SpineColor? color)
        {
            if (color.HasValue == false)
            {
                throw new SpineConversionException("색 타임라인 키에 색이 없다(곡선을 변환할 수 없다).");
            }

            return color.Value;
        }

        #endregion

        #region 회전 타임라인

        public static void ConvertRotateTimeline3xTo4x(SpineSkeleton skeleton)
        {
            ForEachRotateTimeline(skeleton, ConvertRotateTimeline3xTo4x);
        }

        public static void ConvertRotateTimeline4xTo3x(SpineSkeleton skeleton)
        {
            ForEachRotateTimeline(skeleton, ConvertRotateTimeline4xTo3x);
        }

        private static void ForEachRotateTimeline(SpineSkeleton skeleton, Func<List<SpineTimelineFrame>, List<SpineTimelineFrame>> convert)
        {
            foreach (SpineAnimation animation in skeleton.Animations)
            {
                foreach (SpineTimelineSet timelines in animation.Bones.Values)
                {
                    if (timelines.TryGetValue("rotate", out List<SpineTimelineFrame> rotate) == true)
                    {
                        timelines["rotate"] = convert(rotate);
                    }
                }
            }
        }

        /// <summary>3.x 의 최단 경로 각도 차이를 누적해 4.x 의 펼친(절대) 각도로 바꾼다.</summary>
        private static List<SpineTimelineFrame> ConvertRotateTimeline3xTo4x(List<SpineTimelineFrame> timeline)
        {
            if (timeline.Count < 2)
            {
                return timeline;
            }

            float unwrappedValue = timeline[0].Value1;
            float previousDelta = 0f;
            for (int i = 1; i < timeline.Count; i++)
            {
                float delta = NormalizeRotationDelta(timeline[i].Value1 - timeline[i - 1].Value1);
                delta = ResolveAmbiguousHalfTurn(delta, previousDelta);
                unwrappedValue += delta;
                timeline[i].Value1 = unwrappedValue;
                if (Math.Abs(delta) > RotateEpsilon)
                {
                    previousDelta = delta;
                }
            }

            return timeline;
        }

        private static float NormalizeRotationDelta(float delta)
        {
            // C# 의 float % 는 C 의 fmod 와 같다(부호는 피제수를 따른다).
            delta %= 360f;
            if (delta > 180f)
            {
                delta -= 360f;
            }

            if (delta < -180f)
            {
                delta += 360f;
            }

            return delta;
        }

        private static float ResolveAmbiguousHalfTurn(float delta, float previousDelta)
        {
            if (Math.Abs(Math.Abs(delta) - 180f) > RotateEpsilon)
            {
                return delta;
            }

            if (previousDelta > 0f)
            {
                return 180f;
            }

            if (previousDelta < 0f)
            {
                return -180f;
            }

            return 180f;
        }

        /// <summary>
        /// 4.x 의 큰 회전 구간(±179° 초과)을 3.x 최단 경로로 같은 움직임이 나오게 보조 키로 쪼갠다.
        /// 선형·단조 베지어 구간은 원래 곡선을 De Casteljau 로 분할해 보존한다. 쪼갤 수 없는 구간은 그대로 둔다
        /// (그 경우 <see cref="ConvertCurve4xTo3x"/> 가 rotation_range 로 거부한다).
        /// </summary>
        private static List<SpineTimelineFrame> ConvertRotateTimeline4xTo3x(List<SpineTimelineFrame> timeline)
        {
            if (timeline.Count < 2)
            {
                return timeline;
            }

            var converted = new List<SpineTimelineFrame>(timeline.Count * 2) { timeline[0] };
            for (int i = 1; i < timeline.Count; i++)
            {
                SpineTimelineFrame last = converted[converted.Count - 1];
                last.CurveType = timeline[i - 1].CurveType;
                last.Curve = new List<float>(timeline[i - 1].Curve);
                SpineTimelineFrame target = timeline[i];
                while (true)
                {
                    SpineTimelineFrame current = converted[converted.Count - 1];
                    float delta = target.Value1 - current.Value1;
                    if (delta <= Max3xRotateDelta + RotateEpsilon && delta >= -Max3xRotateDelta - RotateEpsilon)
                    {
                        break;
                    }

                    float splitValue = current.Value1 + (delta > 0f ? Max3xRotateDelta : -Max3xRotateDelta);
                    if (TrySplitRotateSegment(current, target, splitValue, out SpineTimelineFrame leftStart, out SpineTimelineFrame mid) == false)
                    {
                        break;
                    }

                    converted[converted.Count - 1] = leftStart;
                    converted.Add(mid);
                }

                converted.Add(target);
            }

            return converted;
        }

        private static bool TrySplitRotateSegment(SpineTimelineFrame start, SpineTimelineFrame end, float value, out SpineTimelineFrame leftStart, out SpineTimelineFrame mid)
        {
            switch (start.CurveType)
            {
                case SpineCurveType.Linear:
                    return TrySplitLinearRotateSegment(start, end, value, out leftStart, out mid);
                case SpineCurveType.Bezier:
                    return TrySplitBezierRotateSegment(start, end, value, out leftStart, out mid);
                default:
                    leftStart = null;
                    mid = null;
                    return false;
            }
        }

        private static bool TrySplitLinearRotateSegment(SpineTimelineFrame start, SpineTimelineFrame end, float value, out SpineTimelineFrame leftStart, out SpineTimelineFrame mid)
        {
            leftStart = null;
            mid = null;
            float delta = end.Value1 - start.Value1;
            if (Math.Abs(delta) <= RotateEpsilon)
            {
                return false;
            }

            float ratio = (value - start.Value1) / delta;
            if (ratio <= RotateEpsilon || ratio >= 1f - RotateEpsilon)
            {
                return false;
            }

            leftStart = CloneFrame(start);
            leftStart.CurveType = SpineCurveType.Linear;
            leftStart.Curve.Clear();
            mid = MakeRotateFrame(start.Time + (end.Time - start.Time) * ratio, value);
            return true;
        }

        private static bool TrySplitBezierRotateSegment(SpineTimelineFrame start, SpineTimelineFrame end, float value, out SpineTimelineFrame leftStart, out SpineTimelineFrame mid)
        {
            if (start.Curve.Count < 4)
            {
                return TrySplitLinearRotateSegment(start, end, value, out leftStart, out mid);
            }

            leftStart = null;
            mid = null;
            var p0 = new BezierPoint(start.Time, start.Value1);
            var p1 = new BezierPoint(start.Curve[0], start.Curve[1]);
            var p2 = new BezierPoint(start.Curve[2], start.Curve[3]);
            var p3 = new BezierPoint(end.Time, end.Value1);
            if (TryFindBezierTForValue(p0, p1, p2, p3, value, out float t) == false)
            {
                return false;
            }

            BezierPoint p01 = Lerp(p0, p1, t);
            BezierPoint p12 = Lerp(p1, p2, t);
            BezierPoint p23 = Lerp(p2, p3, t);
            BezierPoint p012 = Lerp(p01, p12, t);
            BezierPoint p123 = Lerp(p12, p23, t);
            BezierPoint p0123 = Lerp(p012, p123, t);
            if (p0123.X <= start.Time + RotateEpsilon || p0123.X >= end.Time - RotateEpsilon)
            {
                return false;
            }

            leftStart = CloneFrame(start);
            leftStart.CurveType = SpineCurveType.Bezier;
            leftStart.Curve = new List<float> { p01.X, p01.Y, p012.X, p012.Y };

            mid = MakeRotateFrame(p0123.X, p0123.Y);
            mid.CurveType = SpineCurveType.Bezier;
            mid.Curve = new List<float> { p123.X, p123.Y, p23.X, p23.Y };
            return true;
        }

        private static bool TryFindBezierTForValue(BezierPoint p0, BezierPoint p1, BezierPoint p2, BezierPoint p3, float value, out float result)
        {
            const int samples = 100;
            float previousT = 0f;
            float previousY = p0.Y - value;
            for (int i = 1; i <= samples; i++)
            {
                float currentT = (float)i / samples;
                float currentY = EvaluateBezier(p0, p1, p2, p3, currentT).Y - value;
                if (Math.Abs(currentY) <= RotateEpsilon)
                {
                    result = currentT;
                    return result > RotateEpsilon && result < 1f - RotateEpsilon;
                }

                if (previousY * currentY < 0f)
                {
                    float low = previousT;
                    float high = currentT;
                    for (int step = 0; step < 24; step++)
                    {
                        float middle = (low + high) * 0.5f;
                        float middleY = EvaluateBezier(p0, p1, p2, p3, middle).Y - value;
                        if (previousY * middleY <= 0f)
                        {
                            high = middle;
                        }
                        else
                        {
                            low = middle;
                            previousY = middleY;
                        }
                    }

                    result = (low + high) * 0.5f;
                    return result > RotateEpsilon && result < 1f - RotateEpsilon;
                }

                previousT = currentT;
                previousY = currentY;
            }

            result = 0f;
            return false;
        }

        private readonly struct BezierPoint
        {
            public readonly float X;
            public readonly float Y;

            public BezierPoint(float x, float y)
            {
                X = x;
                Y = y;
            }
        }

        private static BezierPoint Lerp(BezierPoint a, BezierPoint b, float t)
        {
            return new BezierPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        }

        private static BezierPoint EvaluateBezier(BezierPoint p0, BezierPoint p1, BezierPoint p2, BezierPoint p3, float t)
        {
            BezierPoint p01 = Lerp(p0, p1, t);
            BezierPoint p12 = Lerp(p1, p2, t);
            BezierPoint p23 = Lerp(p2, p3, t);
            BezierPoint p012 = Lerp(p01, p12, t);
            BezierPoint p123 = Lerp(p12, p23, t);
            return Lerp(p012, p123, t);
        }

        private static SpineTimelineFrame MakeRotateFrame(float time, float value)
        {
            return new SpineTimelineFrame { Time = time, Value1 = value, CurveType = SpineCurveType.Linear };
        }

        // 원본은 프레임을 값으로 복사한다 — 회전 프레임에서 의미 있는 값만이 아니라 전부 옮긴다.
        private static SpineTimelineFrame CloneFrame(SpineTimelineFrame source)
        {
            return new SpineTimelineFrame
            {
                Time = source.Time,
                Str1 = source.Str1,
                Str2 = source.Str2,
                Int1 = source.Int1,
                Value1 = source.Value1,
                Value2 = source.Value2,
                Value3 = source.Value3,
                Value4 = source.Value4,
                Value5 = source.Value5,
                Value6 = source.Value6,
                Color1 = source.Color1,
                Color2 = source.Color2,
                CurveType = source.CurveType,
                Curve = new List<float>(source.Curve),
                Inherit = source.Inherit,
                SequenceMode = source.SequenceMode,
                BendPositive = source.BendPositive,
                Compress = source.Compress,
                Stretch = source.Stretch,
                Vertices = new List<float>(source.Vertices),
                Offsets = new List<KeyValuePair<string, int>>(source.Offsets),
            };
        }

        #endregion

        #region 경로 간격·제약조건 순서

        /// <summary>3.x 에는 proportional 간격이 없다 — length 로 바꾼다(원본과 같은 손실 변환).</summary>
        public static void ConvertSpacingMode4xTo3x(SpineSkeleton skeleton)
        {
            foreach (SpinePathConstraint path in skeleton.PathConstraints)
            {
                if (path.SpacingMode == SpineSpacingMode.Proportional)
                {
                    path.SpacingMode = SpineSpacingMode.Length;
                }
            }
        }

        /// <summary>4.2 는 physics 까지 한 순서 공간을 쓴다. 4.1 이하로 내릴 때 ik·transform·path 의 order 를 빈틈 없이 0..n-1 로 다시 매긴다.</summary>
        public static void ConvertOrder42ToBelow(SpineSkeleton skeleton)
        {
            var orders = new SortedSet<int>();
            foreach (SpineIkConstraint ik in skeleton.IkConstraints)
            {
                orders.Add(ik.Order);
            }

            foreach (SpineTransformConstraint transform in skeleton.TransformConstraints)
            {
                orders.Add(transform.Order);
            }

            foreach (SpinePathConstraint path in skeleton.PathConstraints)
            {
                orders.Add(path.Order);
            }

            var rank = new Dictionary<int, int>();
            foreach (int order in orders)
            {
                rank[order] = rank.Count;
            }

            foreach (SpineIkConstraint ik in skeleton.IkConstraints)
            {
                ik.Order = rank[ik.Order];
            }

            foreach (SpineTransformConstraint transform in skeleton.TransformConstraints)
            {
                transform.Order = rank[transform.Order];
            }

            foreach (SpinePathConstraint path in skeleton.PathConstraints)
            {
                path.Order = rank[path.Order];
            }
        }

        #endregion
    }
}
