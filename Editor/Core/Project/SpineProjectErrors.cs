using System;

namespace NKStudio.SpineEventInjector.Project
{
    /// <summary>파일이 예상한 형식과 다르다(지원 범위 밖이거나 손상).</summary>
    public sealed class SpineFormatException : Exception
    {
        public SpineFormatException(string message) : base(message) { }
    }

    /// <summary>요청이 잘못됐다(없는 애니메이션, 겹치는 키 등). 파일은 건드리지 않는다.</summary>
    public sealed class SpineEventUsageException : Exception
    {
        public SpineEventUsageException(string message) : base(message) { }
    }
}
