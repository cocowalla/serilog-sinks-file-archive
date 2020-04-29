using System;
using System.Collections.Generic;
using Serilog.Debugging;
using Shouldly;
using Xunit;

namespace Serilog.Sinks.File.Archive.Tests
{
    public class TokenExpanderTests
    {
        [Fact]
        public void Should_expand_local_date_tokens()
        {
            const string input = "/my/path/{Date:yyyy}/{Date:MM}{Date:dd}";
            var result = TokenExpander.Expand(input);

            var dt = DateTime.Now;
            result.ShouldBe($"/my/path/{dt:yyyy}/{dt:MM}{dt:dd}");
        }

        [Fact]
        public void Should_expand_utc_date_tokens()
        {
            const string input = "/my/path/{UtcDate:yyyy}/{UtcDate:MM}{Date:dd}";
            var result = TokenExpander.Expand(input);

            var dt = DateTime.UtcNow;
            result.ShouldBe($"/my/path/{dt:yyyy}/{dt:MM}{dt:dd}");
        }

        // Test that we write diagnostic logs if unsupported token names are used
        [Fact]
        public void Should_log_unsupported_tokens()
        {
            var messages = new List<string>();
            SelfLog.Enable(x => messages.Add(x));

            const string input = "/my/path/{Myergen:yyyy}/{Meh:MM}";
            var result = TokenExpander.Expand(input);

            // Result should be the unchanged input
            result.ShouldBe(input);

            // Ensure we wrote diagnostic logs
            messages.Count.ShouldBe(2);
            messages.ShouldAllBe(x => x.Contains("Unsupported token"));
            messages.ShouldContain(x => x.EndsWith("Myergen"));
            messages.ShouldContain(x => x.EndsWith("Meh"));
        }
    }
}
