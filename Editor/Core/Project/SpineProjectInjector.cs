using System.Collections.Generic;
using System.Linq;
using static NKStudio.SpineEventInjector.Project.SpineProjectWriter;

using NKStudio.SpineEventInjector;

namespace NKStudio.SpineEventInjector.Project
{
    /// <summary>
    /// .spine 객체 그래프의 이벤트 편집. 바꾼 결과는 <see cref="SpineProjectWriter.Serialize"/> 로 얻는다.
    /// 추가 연산은 파이썬 PoC 의 Injector 를 그대로 옮겼다(같은 입력이면 같은 바이트).
    /// </summary>
    public sealed class SpineProjectInjector
    {
        static readonly string[] TimelineDeep = { "Timeline", "List", "KeyList", "Keyframe", "Event" };

        readonly SpineProjectDocument _doc;
        SpineProjectDocument _template;
        public readonly List<string> Log = new List<string>();

        public SpineProjectInjector(SpineProjectDocument doc) => _doc = doc;

        SpineProjectDocument Template() => _template ?? (_template = SpineProjectTemplates.For(_doc.Schema, _doc.Version));

        Node Skeleton()
        {
            if (_doc.ScopedSkeleton is Node scoped)
            {
                return scoped;
            }

            List<Node> skeletons = _doc.Of("Skeleton").ToList();
            if (skeletons.Count != 1)
            {
                throw new SpineEventUsageException(Loc.T("스켈레톤이 {0}개입니다(스켈레톤 하나인 프로젝트만 다룹니다)", skeletons.Count));
            }

            return skeletons[0];
        }

        // ------------------------------------------------------------ 이벤트 정의

        public Node AddEventDef(string name, int intValue = 0, float floatValue = 0f, string stringValue = "")
        {
            if (_doc.EventDef(name) != null)
            {
                throw new SpineEventUsageException(Loc.T("이벤트 정의 '{0}'가 이미 있습니다", name));
            }

            List<(string Name, Node Node)> defs = _doc.EventDefs();
            Node skeleton = Skeleton();
            Node tpl;
            Node folder;
            if (defs.Count > 0)
            {
                tpl = defs[0].Node;
                folder = _doc.Deref(tpl.Value("10"));
            }
            else
            {
                // 정의가 하나도 없다: 같은 스키마의 견본 프로젝트에서 복제하고, 스켈레톤의 이벤트 폴더(필드 40)에 넣는다
                SpineProjectDocument template = Template();
                tpl = template?.EventDefs().FirstOrDefault().Node
                      ?? throw new SpineEventUsageException(Loc.T("파일에 이벤트 정의가 하나도 없습니다(정의 목록·이벤트 폴더 견본이 없어 새로 만들 수 없습니다)"));
                folder = _doc.Deref(skeleton.Value("40"));
            }

            Node def = Clone(tpl, new string[0]);
            if (defs.Count == 0 && folder != null)
            {
                SetSlot(def, "0", new Slot(null, skeleton));
                SetSlot(def, "10", new Slot(_doc.ClassNumber("Folder"), folder));
            }

            SetSlot(def, "2", new Slot(null, NewString(_doc, name)));
            SetRaw(def, "3", Varint(Zigzag(intValue)));
            SetRaw(def, "4", FloatBytes(floatValue));
            SetSlot(def, "5", new Slot(null, NewString(_doc, stringValue ?? string.Empty)));
            if (def.F.ContainsKey("6"))
            {
                SetSlot(def, "6", new Slot(null, NewString(_doc, string.Empty))); // audio 경로: 비움
            }

            // 스켈레톤의 정의 목록과 이벤트 폴더의 자식 목록에 끝으로 붙인다.
            Node evlist = _doc.Deref(skeleton.Value("10"));
            if (evlist == null || folder == null || folder.Cls != "Folder")
            {
                throw new SpineFormatException(Loc.T("이벤트 정의 목록·폴더 구조가 예상과 다릅니다"));
            }

            Node children = _doc.Deref(folder.Value("5"));
            int cnum = _doc.ClassNumber("EventData");
            SetList(evlist, ListSlots(evlist).Concat(new[] { new Slot(cnum, def) }).ToList());
            SetList(children, ListSlots(children).Concat(new[] { new Slot(cnum, def) }).ToList());
            SetLookup(def, "2", name);
            SetLookup(def, "3", Zigzag(intValue));
            SetLookup(def, "4", floatValue);
            SetLookup(def, "5", stringValue ?? string.Empty);
            Log.Add(Loc.T("이벤트 정의 '{0}' 추가", name));
            return def;
        }

        /// <summary>정의의 기본값을 바꾼다(이름 변경 포함). 키들은 자기 값을 따로 들고 있어 바뀌지 않는다.</summary>
        public void SetEventDef(Node def, string name, int intValue, float floatValue, string stringValue)
        {
            string old = (string)def.Value("2");
            if (name != old)
            {
                if (_doc.EventDef(name) != null)
                {
                    throw new SpineEventUsageException(Loc.T("이벤트 정의 '{0}'가 이미 있습니다", name));
                }

                SetSlot(def, "2", new Slot(null, NewString(_doc, name)));
                SetLookup(def, "2", name);
            }

            if (Unzigzag((long)def.Value("3")) != intValue)
            {
                SetRaw(def, "3", Varint(Zigzag(intValue)));
                SetLookup(def, "3", Zigzag(intValue));
            }

            if (FloatBytes((float)def.Value("4")).SequenceEqual(FloatBytes(floatValue)) == false)
            {
                SetRaw(def, "4", FloatBytes(floatValue));
                SetLookup(def, "4", floatValue);
            }

            if (((string)def.Value("5") ?? string.Empty) != (stringValue ?? string.Empty))
            {
                SetSlot(def, "5", new Slot(null, NewString(_doc, stringValue ?? string.Empty)));
                SetLookup(def, "5", stringValue ?? string.Empty);
            }

            Log.Add(Loc.T("이벤트 정의 '{0}' 수정", old));
        }

        /// <summary>쓰는 키가 없는 정의를 지운다(정의 목록·이벤트 폴더에서 뺀다).</summary>
        public void RemoveEventDef(Node def)
        {
            string name = (string)def.Value("2");
            foreach (Node anim in _doc.Of("Animation")) // 에디터 상태의 복사본이 쓰는 정의도 지우지 않는다
            {
                if (_doc.EventGroup(anim).Timelines.Any(tl => _doc.TimelineDef(tl) == def && ListSlots(KeyListOf(tl)).Count > 0))
                {
                    throw new SpineEventUsageException(Loc.T("'{0}' 정의를 쓰는 키가 있어 지울 수 없습니다", name));
                }
            }

            Node skeleton = Skeleton();
            Node evlist = _doc.Deref(skeleton.Value("10"));
            Node folder = _doc.Deref(def.Value("10"));
            SetList(evlist, ListSlots(evlist).Where(s => s.Obj != def).ToList());
            if (folder != null)
            {
                Node children = _doc.Deref(folder.Value("5"));
                SetList(children, ListSlots(children).Where(s => s.Obj != def).ToList());
            }

            Log.Add(Loc.T("이벤트 정의 '{0}' 삭제", name));
        }

        static void SetLookup(Node node, string key, object value)
        {
            FieldValue f = node.F[key];
            node.F[key] = new FieldValue(f.At, f.End, value);
        }

        // ------------------------------------------------------------ 이벤트 키

        /// <summary>
        /// 키 하나 추가. data(EventData)를 안 주면 앞 키의 정의(새 타임라인이면 첫 정의).
        /// 값(int·float·string)을 안 주면 정의의 기본값. 돌려주는 값: (키, 정의)
        /// </summary>
        public (Node Key, Node Def) AddKey(string animName, float frame, Node data = null, int? intValue = null, float? floatValue = null, string stringValue = null)
        {
            Node anim = _doc.Animation(animName) ?? throw new SpineEventUsageException(Loc.T("애니메이션 '{0}'이 없습니다", animName));
            (Node group, List<Node> timelines) = _doc.EventGroup(anim);
            if (data == null)
            {
                List<ProjectEventKey> keys = _doc.AnimationEventKeys(anim);
                if (keys.Count > 0)
                {
                    List<ProjectEventKey> before = keys.Where(k => k.Frame <= frame).ToList();
                    Node knode = (before.Count > 0 ? before[before.Count - 1] : keys[0]).KeyNode;
                    data = _doc.Deref(_doc.Deref(knode.Value(_doc.EventKey(knode))).Value("1"));
                }
                else
                {
                    List<(string Name, Node Node)> defs = _doc.EventDefs();
                    if (defs.Count == 0)
                    {
                        throw new SpineEventUsageException(Loc.T("이벤트 정의가 없습니다. 정의를 먼저 만드세요"));
                    }

                    data = defs[0].Node;
                }
            }

            Node tl = timelines.FirstOrDefault(t => _doc.TimelineDef(t) == data);
            Node keylist;
            List<Slot> slots;
            Node src;
            int idx;
            bool fresh;
            if (tl == null)
            {
                // 이 정의의 타임라인이 없다: 그룹(없으면 그룹째)에 새로 만든다
                tl = group == null ? AddEventGroup(anim, data).Timeline : AddDefTimeline(group, timelines[0], data);
                keylist = Child(tl, SpineProjectDocument.KeysKey(tl));
                slots = ListSlots(keylist);
                src = slots[0].Obj;
                idx = 0;
                fresh = true;
            }
            else
            {
                keylist = _doc.KeyList(tl);
                slots = ListSlots(keylist);
                List<float> times = slots.Select(s => KeyTime(s.Obj)).ToList();
                if (times.Any(t => t == frame))
                {
                    // Spine은 겹친 키 하나를 읽으면서 버린다(오라클로 확인)
                    throw new SpineEventUsageException(Loc.T("{0} {1}프레임에 이미 '{2}' 키가 있습니다", animName, frame, data.Value("2")));
                }

                idx = times.Count(t => t <= frame);
                src = (idx > 0 ? slots[idx - 1] : slots[0]).Obj;
                fresh = false;
            }

            Node key = Clone(src, new[] { "Event" });
            SetRaw(key, SpineProjectDocument.TimeKey(key), FloatBytes(frame));
            SetLookup(key, SpineProjectDocument.TimeKey(key), frame);
            Node ev = Child(key, _doc.EventKey(key));
            SetSlot(ev, "1", new Slot(null, data));
            int wantInt = intValue ?? Unzigzag((long)data.Value("3"));
            float wantFloat = floatValue ?? (float)data.Value("4");
            string wantString = stringValue ?? ((string)data.Value("5") ?? string.Empty);
            int haveInt = Unzigzag((long)ev.Value("2"));
            float haveFloat = (float)ev.Value("3");
            string haveString = (string)ev.Value("4") ?? string.Empty;
            if (wantInt != haveInt)
            {
                SetRaw(ev, "2", Varint(Zigzag(wantInt)));
            }

            if (FloatBytes(wantFloat).SequenceEqual(FloatBytes(haveFloat)) == false)
            {
                SetRaw(ev, "3", FloatBytes(wantFloat));
            }

            if (stringValue != null)
            {
                SetSlot(ev, "4", new Slot(null, NewString(_doc, stringValue)));
            }
            else if (wantString != haveString)
            {
                // 정의의 문자열 객체를 함께 쓴다(Spine과 같게)
                Node defString = Child(data, "5");
                SetSlot(ev, "4", new Slot(null, defString ?? NewString(_doc, wantString)));
            }

            if (fresh)
            {
                // 새 타임라인: 견본 키를 이 키로 바꾼다
                SetList(keylist, new List<Slot> { new Slot(slots[0].Prefix, key) });
            }
            else
            {
                slots.Insert(idx, new Slot(slots[0].Prefix, key));
                SetList(keylist, slots);
            }

            Log.Add(Loc.T("{0} {1}프레임 키 추가({2})", animName, frame, data.Value("2")));
            return (key, data);
        }

        /// <summary>키 하나를 지운다. 타임라인이 비면 타임라인을, 그룹에 타임라인이 없으면 그룹까지 뺀다.</summary>
        public void RemoveKey(string animName, Node key)
        {
            Node anim = _doc.Animation(animName) ?? throw new SpineEventUsageException(Loc.T("애니메이션 '{0}'이 없습니다", animName));
            (Node group, List<Node> timelines) = _doc.EventGroup(anim);
            foreach (Node tl in timelines)
            {
                Node keylist = KeyListOf(tl);
                List<Slot> slots = ListSlots(keylist);
                int at = slots.FindIndex(s => s.Obj == key);
                if (at < 0)
                {
                    continue;
                }

                slots.RemoveAt(at);
                if (slots.Count > 0)
                {
                    SetList(keylist, slots);
                    Log.Add(Loc.T("{0} 키 삭제", animName));
                    return;
                }

                // 타임라인이 비었다
                Node tlist = _doc.Deref(group.Value("2"));
                List<Slot> rest = ListSlots(tlist).Where(s => s.Obj != tl).ToList();
                if (rest.Count > 0)
                {
                    SetList(tlist, rest);
                }
                else
                {
                    Node groups = _doc.Deref(anim.Value("2"));
                    SetList(groups, ListSlots(groups).Where(s => s.Obj != group).ToList());
                }

                Log.Add(Loc.T("{0} 키 삭제(빈 이벤트 타임라인 제거)", animName));
                return;
            }

            throw new SpineEventUsageException(Loc.T("{0}에서 지울 키를 찾지 못했습니다", animName));
        }

        /// <summary>키의 값(int·float·string)만 바꾼다. 문자열이 정의 기본값과 같으면 정의의 문자열 객체를 함께 쓴다(Spine과 같게).</summary>
        public void SetKeyValues(Node key, int intValue, float floatValue, string stringValue)
        {
            Node ev = _doc.Deref(key.Value(_doc.EventKey(key)));
            Node data = _doc.Deref(ev.Value("1"));
            if (Unzigzag((long)ev.Value("2")) != intValue)
            {
                SetRaw(ev, "2", Varint(Zigzag(intValue)));
            }

            if (FloatBytes((float)ev.Value("3")).SequenceEqual(FloatBytes(floatValue)) == false)
            {
                SetRaw(ev, "3", FloatBytes(floatValue));
            }

            string want = stringValue ?? string.Empty;
            if (((string)ev.Value("4") ?? string.Empty) != want)
            {
                Node defString = want == ((string)data.Value("5") ?? string.Empty) ? Child(data, "5") : null;
                SetSlot(ev, "4", new Slot(null, defString ?? NewString(_doc, want)));
            }

            Log.Add(Loc.T("키 값 수정({0})", data.Value("2")));
        }

        /// <summary>애니메이션에서 (프레임, 정의 이름)이 같은 키를 찾는다.</summary>
        public Node FindKey(string animName, float frame, string defName)
        {
            Node anim = _doc.Animation(animName) ?? throw new SpineEventUsageException(Loc.T("애니메이션 '{0}'이 없습니다", animName));
            ProjectEventKey hit = _doc.AnimationEventKeys(anim).FirstOrDefault(k => k.Frame == frame && k.Name == defName);
            return hit?.KeyNode ?? throw new SpineEventUsageException(Loc.T("{0} {1}프레임에 '{2}' 키가 없습니다", animName, frame, defName));
        }

        Node KeyListOf(Node tl) => Child(tl, SpineProjectDocument.KeysKey(tl)) ?? _doc.KeyList(tl);

        float KeyTime(Node key) => (float)key.Value(SpineProjectDocument.TimeKey(key));

        // ------------------------------------------------------------ 타임라인·그룹

        void ResetTimeline(Node tl, Node data)
        {
            // 복제한 이벤트 타임라인을 data 정의용으로: 정의 참조를 바꾸고 견본 키 하나만 남긴다(AddKey 가 바꾼다)
            SetSlot(tl, SpineProjectDocument.DefKey(tl), new Slot(null, data));
            Node keylist = Child(tl, SpineProjectDocument.KeysKey(tl));
            SetList(keylist, ListSlots(keylist).Take(1).ToList());
        }

        Node AddDefTimeline(Node group, Node tpl, Node data)
        {
            // 이벤트 그룹에 data 정의용 타임라인을 하나 더 만든다(Spine도 정의마다 타임라인을 따로 둔다)
            Node tl = Clone(tpl, TimelineDeep);
            ResetTimeline(tl, data);
            Node tlist = _doc.Deref(group.Value("2"));
            List<Slot> cur = ListSlots(tlist);
            SetList(tlist, cur.Concat(new[] { new Slot(cur[0].Prefix, tl) }).ToList());
            Log.Add(Loc.T("이벤트 타임라인 생성({0})", data.Value("2")));
            return tl;
        }

        (Node Group, Node Timeline) AddEventGroup(Node anim, Node data)
        {
            // 이벤트 키가 없는 애니메이션에 다른 애니메이션의 이벤트 타임라인 그룹을 견본으로 복제해 붙인다
            Node tpl = null;
            foreach (Node a in _doc.Animations())
            {
                Node g = _doc.EventGroup(a).Group;
                if (g != null)
                {
                    tpl = g;
                    break;
                }
            }

            if (tpl == null)
            {
                // 파일에 이벤트 키가 하나도 없다: 같은 스키마의 견본 프로젝트에서 그룹째 복제한다
                SpineProjectDocument template = Template();
                if (template != null)
                {
                    tpl = template.Animations().Select(a => template.EventGroup(a).Group).FirstOrDefault(g => g != null);
                }
            }

            if (tpl == null)
            {
                throw new SpineEventUsageException(Loc.T("파일에 이벤트 키가 하나도 없습니다(견본 타임라인이 없어 새로 만들 수 없습니다)"));
            }

            Node group = Clone(tpl, new[] { "TimelineGroup" }.Concat(TimelineDeep).ToList());
            if (group.F.ContainsKey("0"))
            {
                SetSlot(group, "0", new Slot(null, anim)); // 4.3.03부터 그룹이 애니메이션을 역참조하지 않는다
            }

            Node tlist = Child(group, "2");
            List<Slot> tls = ListSlots(tlist).Where(s => SpineProjectDocument.IsEventTimeline(s.Obj)).ToList();
            SetList(tlist, tls.Take(1).ToList());
            Node tl = tls[0].Obj;
            ResetTimeline(tl, data);
            Node groups = _doc.Deref(anim.Value("2"));
            List<Slot> cur = ListSlots(groups);
            SetList(groups, cur.Concat(new[] { new Slot(cur.Count > 0 ? cur[0].Prefix : _doc.ClassNumber("TimelineGroup"), group) }).ToList());
            Log.Add(Loc.T("{0}에 이벤트 타임라인 그룹 생성({1})", anim.Value("1"), data.Value("2")));
            return (group, tl);
        }
    }
}
