using System;
using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    public static class FanSequenceTests
    {

        private static bool Seq(List<string> log, int failCall)
        {
            int n = 0;
            return FanSequence.SetPercent(delegate(string m, ulong v)
            {
                n++;
                log.Add(m + ":" + v.ToString("X"));
                return n != failCall;
            }, 50, 60);
        }

        [Test]
        public static void SetPercent_AllOk_ReturnsTrueWithoutAuto()
        {
            List<string> log = new List<string>();
            Assert.True(Seq(log, 0), "ok");
            Assert.Equal(3, log.Count);
            Assert.Equal("SetGamingFanSpeed:3C04", log[2]);
        }

        [Test]
        public static void SetPercent_SpeedFails_WritesAutoLastAndReturnsFalse()
        {
            List<string> log = new List<string>();
            Assert.True(!Seq(log, 2), "should fail");
            Assert.Equal("SetGamingFanBehavior:410009", log[log.Count - 1]);
        }

        [Test]
        public static void SetPercent_BehaviorFails_WritesAutoAndReturnsFalse()
        {
            List<string> log = new List<string>();
            Assert.True(!Seq(log, 1), "should fail");
            Assert.Equal(2, log.Count);
            Assert.Equal("SetGamingFanBehavior:410009", log[1]);
        }
    }
}
