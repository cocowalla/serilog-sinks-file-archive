using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Serilog.Debugging;

namespace Serilog.Sinks.File.Archive
{
    /// <summary>
    /// Expands tokens in a string. Tokens should be in the form "{Name:format}", where "Name" is the name of a supported token, and
    /// "format" is a format string for that token.
    ///
    /// Supported tokens:
    /// Date     Uses the specified format string to insert a local date/time string at the location in the string
    /// UtcDate  Uses the specified format string to insert a UTC date/time string at the location in the string
    /// </summary>
    internal static class TokenExpander
    {
        private static readonly Regex TokenRegex = new Regex("{(?<Token>(?<Name>[a-zA-Z]+):(?<Format>[^}]+))}", RegexOptions.Compiled | RegexOptions.ECMAScript);

        private static readonly IDictionary<string, Func<string, Token, string>> Expanders = new Dictionary<string, Func<string, Token, string>>
        {
            { "Date", (source, token) => DateTime.Now.ToString(token.Format) },
            { "UtcDate", (source, token) => DateTime.UtcNow.ToString(token.Format) }
        };

        public static string Expand(string source)
        {
            int startIdx = 0;
            while (TryFindNextToken(source, startIdx, out var token))
            {
                // We found a token - is it supported?
                if (Expanders.TryGetValue(token.Name, out var expander))
                {
                    var expanded = expander(source, token);
                    source = source.Remove(token.StartIdx, token.Length);
                    source = source.Insert(token.StartIdx, expanded);

                    startIdx = token.StartIdx + expanded.Length;
                }
                else
                {
                    SelfLog.WriteLine("Unsupported token: {0}", token.Name);

                    startIdx = token.StartIdx + token.Length;
                }
            }

            return source;
        }
        public static bool IsTokenised(string source)
        {
            return TryFindNextToken(source, 0, out var _);
        }

        private static bool TryFindNextToken(string source, int startIdx, out Token token)
        {
            var match = TokenRegex.Match(source, startIdx);

            if (!match.Success)
            {
                token = null;
                return false;
            }

            token = new Token
            {
                StartIdx = match.Index,
                Length = match.Length,
                Name = match.Groups["Name"].Value,
                Format = match.Groups["Format"].Value
            };

            return true;
        }

        private class Token
        {
            public int StartIdx { get; set; }
            public int Length { get; set; }
            public string Name { get; set; }
            public string Format { get; set; }
        }
    }
}
