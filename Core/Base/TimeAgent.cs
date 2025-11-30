using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core;
public static class TimeAgent
{
    private static Stopwatch _timeWatch = Stopwatch.StartNew();

    public static Int64 CurrentTime => _timeWatch.ElapsedMilliseconds;
}