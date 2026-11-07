using System;
using Pcv.Modular.Abstractions;
using Pcv.Modular.Contracts;

namespace Pcv.Official.Guide
{
    /// <summary>
    /// 官方入门指南。整个包没有后台逻辑，只有一张说明卡，
    /// 用 IHostControl 上的两个按钮演示「框架现在能请宿主做事」。
    /// </summary>
    public sealed partial class Entry : IPclModule
    {
        internal IModuleContext Context;
        internal IHostControl Host;

        public void OnLoad(IModuleContext context)
        {
            Context = context;
            context.Services.TryGet(out Host);
        }

        public void OnStart() { }

        public void OnStop() { }
    }
}
