using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ArenaMapPort.Crypto;

namespace ArenaMapPort.BundleCrypt
{
    /// <summary>The command line is wrong: exit code 2 and a "USAGE" line.</summary>
    internal sealed class UsageException : Exception
    {
        public UsageException(string message) : base(message) { }
    }

    /// <summary>A command could not do its job: exit code 1 and one "FAIL" line (the message is the reason).</summary>
    internal sealed class ToolFailure : Exception
    {
        public ToolFailure(string message) : base(message) { }
    }

    /// <summary>
    /// Entry point of the build-machine tool (docs/PLAN-ENCRYPTION.md TOOL-1). Exit codes: 0 success (one "OK ..." line), 1 failure (one
    /// "FAIL ..." line on stderr), 2 usage (a "USAGE ..." line on stderr). Key material is never printed. The only command that writes
    /// binary data to stdout is read-range (every message then goes to stderr), so a validator can pipe it.
    /// Proved by tools/tests/test_bundle_crypt_tool.py, which runs the built executable on synthetic bundles and a synthetic Arena folder.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] argv)
        {
            try
            {
                if (argv.Length == 0)
                {
                    Console.Error.WriteLine(Commands.UsageText);
                    return 2;
                }
                if (argv[0] == "--help" || argv[0] == "-h" || argv[0] == "help")
                {
                    Console.Out.WriteLine(Commands.UsageText);
                    return 0;
                }
                Args args = Args.Parse(argv, 1);
                return Commands.Run(argv[0], args);
            }
            catch (UsageException ex)
            {
                Console.Error.WriteLine("USAGE " + ex.Message + " (ArenaMapPort.BundleCrypt help lists the commands)");
                return 2;
            }
            catch (ToolFailure ex)
            {
                Console.Error.WriteLine("FAIL " + ex.Message);
                return 1;
            }
            catch (AmpbException ex)
            {
                Console.Error.WriteLine("FAIL " + ex.Kind + ": " + ex.Message);
                return 1;
            }
            catch (KeyFileException ex)
            {
                Console.Error.WriteLine("FAIL key file: " + ex.Message);
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL unexpected " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }
    }

    /// <summary>
    /// The parsed command line: "--name value" or "--name=value" options, flags without a value, repeated options (--bundle) and positional
    /// arguments. An unknown option is a usage error, so a typo never passes silently. "--map" is an alias of "--map-key", "--arena-dir" of "--arena".
    /// </summary>
    internal sealed class Args
    {
        private static readonly HashSet<string> ValueOptions = new HashSet<string>(StringComparer.Ordinal)
        {
            "in", "out", "map-key", "key-store", "arena", "arena-unverified", "built-for", "levels", "manifest", "keys-file",
            "offset", "length", "provider", "chunk-size", "bundle", "out-dir", "plain", "enc", "size-mib",
        };

        private static readonly HashSet<string> FlagOptions = new HashSet<string>(StringComparer.Ordinal)
        {
            "create-key", "verify-chunks", "json", "clip", "yes",
        };

        private readonly Dictionary<string, List<string>> _values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private readonly HashSet<string> _flags = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _positional = new List<string>();

        public IList<string> Positional
        {
            get { return _positional; }
        }

        public static Args Parse(string[] argv, int start)
        {
            Args a = new Args();
            for (int i = start; i < argv.Length; i++)
            {
                string token = argv[i];
                if (!token.StartsWith("--", StringComparison.Ordinal))
                {
                    a._positional.Add(token);
                    continue;
                }
                string name = token.Substring(2);
                string inline = null;
                int eq = name.IndexOf('=');
                if (eq >= 0)
                {
                    inline = name.Substring(eq + 1);
                    name = name.Substring(0, eq);
                }
                if (name == "map") name = "map-key";
                if (name == "arena-dir") name = "arena";
                if (FlagOptions.Contains(name))
                {
                    if (inline != null)
                        throw new UsageException("--" + name + " takes no value");
                    a._flags.Add(name);
                    continue;
                }
                if (!ValueOptions.Contains(name))
                    throw new UsageException("unknown option --" + name);
                string value = inline;
                if (value == null)
                {
                    if (i + 1 >= argv.Length)
                        throw new UsageException("--" + name + " needs a value");
                    value = argv[++i];
                }
                List<string> list;
                if (!a._values.TryGetValue(name, out list))
                {
                    list = new List<string>();
                    a._values[name] = list;
                }
                list.Add(value);
            }
            return a;
        }

        /// <summary>Refuses every option the command does not know (a typo or an option of another command).</summary>
        public void Check(params string[] allowed)
        {
            HashSet<string> ok = new HashSet<string>(allowed, StringComparer.Ordinal);
            foreach (string name in _values.Keys)
                if (!ok.Contains(name))
                    throw new UsageException("--" + name + " does not apply to this command");
            foreach (string name in _flags)
                if (!ok.Contains(name))
                    throw new UsageException("--" + name + " does not apply to this command");
        }

        public bool Has(string name)
        {
            return _values.ContainsKey(name);
        }

        public bool Flag(string name)
        {
            return _flags.Contains(name);
        }

        public string Get(string name)
        {
            List<string> list;
            if (!_values.TryGetValue(name, out list))
                return null;
            if (list.Count > 1)
                throw new UsageException("--" + name + " was given more than once");
            return list[0];
        }

        public string Require(string name)
        {
            string v = Get(name);
            if (v == null)
                throw new UsageException("--" + name + " is required");
            return v;
        }

        public IList<string> GetAll(string name)
        {
            List<string> list;
            return _values.TryGetValue(name, out list) ? list : new List<string>();
        }

        public long GetLong(string name)
        {
            string v = Require(name);
            long n;
            if (!long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n))
                throw new UsageException("--" + name + " must be an integer, not '" + v + "'");
            return n;
        }
    }
}
