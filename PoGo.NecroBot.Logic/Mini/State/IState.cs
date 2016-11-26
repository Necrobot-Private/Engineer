﻿#region using directives

using System.Threading;
using System.Threading.Tasks;

#endregion

namespace PoGo.NecroBot.Logic.Mini.State
{
    public interface IState
    {
        Task<IState> Execute(ISession session, CancellationToken cancellationToken);
    }
}