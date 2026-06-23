using Bbs.Import.Bpq;

return CliRunner.Run(args);

namespace Bbs.Import.Bpq
{
    /// <summary>
    /// The <c>bpq-import</c> command-line front end. A LinBPQ/BPQMail -&gt; pdn-bbs mailbox importer:
    /// it deterministically rebuilds a fresh <c>bbs.db</c> from a BPQ dump directory, preserving the
    /// no-duplicate-transfer guarantees (verbatim BIDs, pre-marked forwarding legs, the message-number
    /// high-water mark). See <c>docs/bpq-import.md</c> for the field mapping and the three rules.
    /// </summary>
    internal static class CliRunner
    {
        private const int ExitOk = 0;
        private const int ExitUsage = 2;
        private const int ExitError = 1;

        public static int Run(string[] args)
        {
            ArgumentNullException.ThrowIfNull(args);

            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintHelp();
                return args.Length == 0 ? ExitUsage : ExitOk;
            }

            try
            {
                var options = ParseOptions(args, out string? parseError);
                if (parseError is not null)
                {
                    Console.Error.WriteLine($"error: {parseError}");
                    Console.Error.WriteLine("Run 'bpq-import --help' for usage.");
                    return ExitUsage;
                }

                if (options is null)
                {
                    PrintHelp();
                    return ExitOk;
                }

                if (options.DryRun)
                {
                    Console.WriteLine($"DRY RUN — reading '{options.SourceDirectory}', writing nothing.");
                }
                else
                {
                    Console.WriteLine($"Rebuilding '{options.TargetDatabase}' from '{options.SourceDirectory}'...");
                }

                ImportReport report = BpqImporter.Run(options, TimeProvider.System);
                Console.WriteLine();
                Console.Write(report.Render());

                if (!options.DryRun)
                {
                    Console.WriteLine(report.HasWarnings
                        ? "Import complete WITH WARNINGS — review them above before cutover."
                        : "Import complete and clean.");
                }

                return ExitOk;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return ExitError;
            }
        }

        private static ImportOptions? ParseOptions(string[] args, out string? error)
        {
            error = null;
            string? source = null;
            string? target = null;
            string? ownCall = null;
            bool force = false;
            bool dryRun = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--source" or "-s":
                        if (!TryNext(args, ref i, out source))
                        {
                            error = $"{arg} requires a directory path.";
                            return null;
                        }

                        break;
                    case "--target" or "-t":
                        if (!TryNext(args, ref i, out target))
                        {
                            error = $"{arg} requires a file path.";
                            return null;
                        }

                        break;
                    case "--own-call":
                        if (!TryNext(args, ref i, out ownCall))
                        {
                            error = "--own-call requires a callsign.";
                            return null;
                        }

                        break;
                    case "--force":
                        force = true;
                        break;
                    case "--dry-run" or "-n":
                        dryRun = true;
                        break;
                    default:
                        error = $"unknown argument '{arg}'.";
                        return null;
                }
            }

            if (source is null)
            {
                error = "--source <bpq-dump-dir> is required.";
                return null;
            }

            if (!dryRun && target is null)
            {
                error = "--target <bbs.db> is required (or pass --dry-run to validate only).";
                return null;
            }

            return new ImportOptions
            {
                SourceDirectory = source,
                TargetDatabase = target ?? Path.Combine(Path.GetTempPath(), "bpq-import-dryrun.db"),
                OwnCallOverride = ownCall,
                Force = force,
                DryRun = dryRun,
            };
        }

        private static bool TryNext(string[] args, ref int i, out string? value)
        {
            if (i + 1 < args.Length)
            {
                value = args[++i];
                return true;
            }

            value = null;
            return false;
        }

        private static bool IsHelp(string arg) => arg is "--help" or "-h" or "help" or "-?" or "/?";

        private static void PrintHelp()
        {
            const string help =
                """
                bpq-import — LinBPQ/BPQMail -> pdn-bbs mailbox importer

                Deterministically rebuilds a fresh bbs.db from a BPQMail dump directory, preserving the
                network's no-duplicate-transfer guarantees: every BID is kept verbatim and the BID dedup
                store is seeded from WFBID.SYS in full; already-forwarded and still-queued legs are
                pre-marked from each message's forw/fbbs bitmaps; and the message-number high-water mark
                is carried over so new local messages never reuse a number already on the network.

                The BPQ source is read-only and never modified, so each run is idempotent and re-runnable;
                "rollback" means simply rebuilding or discarding the generated bbs.db.

                USAGE
                  bpq-import --source <bpq-dump-dir> --target <bbs.db> [options]
                  bpq-import --source <bpq-dump-dir> --dry-run

                REQUIRED
                  -s, --source <dir>    BPQMail dump directory (must contain DIRMES.SYS, WFBID.SYS,
                                        linmail.cfg, and a Mail/ subdirectory of m_*.mes bodies).
                  -t, --target <file>   The bbs.db to create. Refuses to overwrite unless --force.

                OPTIONS
                  -n, --dry-run         Parse, validate and print the summary; write no database.
                      --force           Overwrite an existing target (deletes it + its WAL/SHM first).
                      --own-call <call> BBS callsign to stamp as the BID identity (default: linmail.cfg BBSName).
                  -h, --help            Show this help.

                EXIT CODES
                  0  success            1  runtime error            2  usage error

                EXAMPLES
                  bpq-import -s /opt/oarc/bpq -n
                  bpq-import -s /opt/oarc/bpq -t /var/lib/pdn-bbs/bbs.db

                See docs/bpq-import.md for the field-mapping table and the three correctness rules.
                """;
            Console.WriteLine(help.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
    }
}
