using System;
using System.Collections.Generic;
using System.Linq;

using NKStudio.SpineEventInjector;

namespace NKStudio.SpineEventInjector.Project
{
    /// <summary>
    /// .spine 프로젝트의 이벤트 읽기·쓰기. 키 시간은 파일에 프레임으로 적혀 있으므로 초 = 프레임 / FPS 로 바꾼다.
    /// 쓰기는 차이를 연산 하나씩 적용하고, 연산마다 다시 쓰고 다시 읽는다(그래프의 조회 정보가 늘 새것이 되도록).
    /// </summary>
    public sealed class SpineProjectCodec : IEventCodec
    {
        readonly IReadOnlyList<SpineProjectSchema> _schemas;

        /// <summary>프로젝트 FPS. .spine 에서 읽는 위치를 아직 모르므로 미리보기용 익스포트(JSON skeleton.fps)나 기본 30을 준다.</summary>
        public float Fps = 30f;

        public SpineProjectCodec(IReadOnlyList<SpineProjectSchema> schemas) => _schemas = schemas;

        public EventSourceKind Kind => EventSourceKind.SpineProject;

        /// <summary>스켈레톤이 여럿인 프로젝트에서 읽고 쓸 스켈레톤. 없거나 모르는 이름이면 이름순 첫 번째.</summary>
        public string Skeleton;

        public SpineProjectFile Load(byte[] file)
        {
            SpineProjectFile project = SpineProjectFile.Load(file, _schemas);
            List<string> names = SkeletonNames(project);
            if (names.Count > 1)
            {
                if (Skeleton == null || names.Contains(Skeleton) == false)
                {
                    Skeleton = names[0];
                }

                project.Doc.ScopeSkeleton = Skeleton;
            }

            return project;
        }

        /// <summary>프로젝트의 스켈레톤 이름(이름순).</summary>
        public static List<string> SkeletonNames(SpineProjectFile project) =>
            project.Doc.Of("Skeleton").Select(s => (string)s.Value("1")).Where(n => n != null).OrderBy(n => n, StringComparer.Ordinal).ToList();

        public EventSet Read(byte[] file) => Read(Load(file));

        public EventSet Read(SpineProjectFile project)
        {
            SpineProjectDocument doc = project.Doc;
            var set = new EventSet { Fps = Fps };
            int id = 0;
            foreach ((string name, Node node) in doc.EventDefsInListOrder())
            {
                set.Definitions.Add(new EventDefinition
                {
                    Id = id++,
                    Name = name,
                    Int = SpineProjectWriter.Unzigzag((long)node.Value("3")),
                    Float = (float)node.Value("4"),
                    String = (string)node.Value("5") ?? string.Empty,
                });
            }

            id = 0;
            foreach (Node anim in doc.Animations())
            {
                string animName = doc.AnimationName(anim);
                set.Animations.Add(animName);
                foreach (ProjectEventKey k in BigStack.Run(() => doc.AnimationEventKeys(anim)))
                {
                    set.Keys.Add(new EventKey
                    {
                        Id = id++,
                        Animation = animName,
                        NativeTime = k.Frame,
                        Seconds = k.Frame / Fps,
                        Name = k.Name,
                        Int = k.Int,
                        Float = k.Float,
                        String = k.String ?? string.Empty,
                    });
                }
            }

            return set;
        }

        public byte[] Write(byte[] file, EventSet original, EventSet edited, List<string> log)
        {
            EventSetDiff diff = EventSetDiff.Compute(original, edited);
            if (diff.IsEmpty)
            {
                return file; // 바뀐 것이 없으면 파일을 그대로 둔다
            }

            SpineProjectFile project = Load(file);
            var renamed = diff.ChangedDefinitions.Where(c => c.Before.Name != c.After.Name).ToDictionary(c => c.Before.Name, c => c.After.Name);
            string Current(string name) => renamed.TryGetValue(name, out string n) ? n : name;

            void Step(Action<SpineProjectInjector> op)
            {
                var injector = new SpineProjectInjector(project.Doc);
                BigStack.Run(() => op(injector));
                project.Commit();
                log?.AddRange(injector.Log);
            }

            foreach ((EventDefinition before, EventDefinition after) in diff.ChangedDefinitions)
            {
                Step(inj => inj.SetEventDef(project.Doc.EventDef(before.Name), after.Name, after.Int, after.Float, after.String));
            }

            foreach (EventDefinition def in diff.AddedDefinitions)
            {
                Step(inj => inj.AddEventDef(def.Name, def.Int, def.Float, def.String));
            }

            foreach ((EventKey before, EventKey after) in diff.RetunedKeys)
            {
                Step(inj => inj.SetKeyValues(inj.FindKey(before.Animation, before.NativeTime, Current(before.Name)), after.Int, after.Float, after.String));
            }

            // 새 키를 먼저 넣고 원본 키를 나중에 지운다(지우다가 견본 타임라인이 사라지지 않게)
            foreach (EventKey key in diff.AddedKeys.Concat(diff.MovedKeys.Select(m => m.After)))
            {
                float frame = (float)Math.Round(key.Seconds * edited.Fps, 4);
                Step(inj => inj.AddKey(key.Animation, frame, project.Doc.EventDef(key.Name) ?? throw new SpineEventUsageException(Loc.T("이벤트 정의 '{0}'가 없습니다", key.Name)),
                    key.Int, key.Float, key.String));
            }

            foreach (EventKey key in diff.RemovedKeys.Concat(diff.MovedKeys.Select(m => m.Before)))
            {
                Step(inj => inj.RemoveKey(key.Animation, inj.FindKey(key.Animation, key.NativeTime, Current(key.Name))));
            }

            foreach (EventDefinition def in diff.RemovedDefinitions)
            {
                Step(inj => inj.RemoveEventDef(project.Doc.EventDef(Current(def.Name))));
            }

            return project.ToFileBytes();
        }
    }
}
