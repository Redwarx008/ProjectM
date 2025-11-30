using Core.Data;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Core;
public class TimeSystem
{


    private Int64 _lastTime = 0;

    public Int64 DeltaTime { get; private set; }

    public bool IsPaused { get; private set; } = false;

    public void Update()
    {
        UpdateDeltaTime();
        if (IsPaused)
        {
            return;
        }


    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateDeltaTime()
    {
        Int64 currentTime = TimeAgent.CurrentTime;
        DeltaTime = currentTime - _lastTime;
        _lastTime = currentTime;
    }

    /// <summary>
    /// Process time progression using deterministic fixed-point math
    /// </summary>
    private void ProcessTimeTicks()
    {
        // Convert real-time to game time (deterministic)
        //FixedPoint64 speedMultiplier = GetSpeedMultiplier(gameSpeedLevel);
        //FixedPoint64 gameTimeDelta = FixedPoint64.FromFloat(DeltaTime) * speedMultiplier * hoursPerSecond;

        //accumulator += gameTimeDelta;

        //// Process full hours
        //while (accumulator >= FixedPoint64.One)
        //{
        //    accumulator -= FixedPoint64.One;
        //    AdvanceHour();
        //}
    }
}