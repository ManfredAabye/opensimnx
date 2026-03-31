using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using log4net;
using MySql.Data.MySqlClient;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Framework.Serialization;
using OpenSim.Server.Handlers.Base;

namespace OpenSim.Addons.SqlDataBackup
{
	public class SqlDataBackup : ServiceConnector
	{
		private enum ConflictMode
		{
			Replace,
			Skip,
			Error,
			MergeReplace,
			MergeSkip
		}

		private enum CheckScope
		{
			Source,
			Target,
			Both
		}

		private sealed class TableComparisonResult
		{
			public bool SourceExists;
			public bool TargetExists;
			public bool SchemaMatches;
			public bool RowCountMatches;
			public bool ChecksumMatches;
			public bool SourceChecksumAvailable;
			public bool TargetChecksumAvailable;
			public long SourceRowCount;
			public long TargetRowCount;
			public string SourceChecksum;
			public string TargetChecksum;
			public string Summary;

			public bool IsIdentical
			{
				get
				{
					if (!SourceExists || !TargetExists)
						return false;

					bool checksumComparable = SourceChecksumAvailable && TargetChecksumAvailable;
					return SchemaMatches && RowCountMatches && (!checksumComparable || ChecksumMatches);
				}
			}
		}

		private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
		private static readonly Regex s_safeTableNameRegex = new Regex("^[A-Za-z0-9_]+$", RegexOptions.Compiled);
		private static readonly Regex s_autoIncrementRegex = new Regex("AUTO_INCREMENT=\\d+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
		private const string ModuleVersion = "1.0.0 Alpha";
		private const string OtbExtension = ".otb";
		private const long DefaultMaxSingleTableExportBytes = 0;
		private const long DefaultMaxOtbPartBytes = 2048L * 1024L * 1024L;
		private const int DefaultRemoteTimeoutSeconds = 120;
		private static readonly string[] s_helpTextLines = new string[]
		{
			"sqlbackup list",
			"sqlbackup export <table>",
			"sqlbackup export <table> <datei.otb|url>",
			"sqlbackup export all <ordner|url>",
			"sqlbackup import replace <table|all> <datei.otb|ordner|url>",
			"sqlbackup import skip <table|all> <datei.otb|ordner|url>",
			"sqlbackup import error <table|all> <datei.otb|ordner|url>",
			"sqlbackup import merge-replace <table|all> <datei.otb|ordner|url>",
			"sqlbackup import merge-skip <table|all> <datei.otb|ordner|url>",
			"sqlbackup copy replace <table|all>",
			"sqlbackup copy skip <table|all>",
			"sqlbackup copy error <table|all>",
			"sqlbackup copy merge-replace <table|all>",
			"sqlbackup copy merge-skip <table|all>",
			"sqlbackup compare <table|all>",
			"sqlbackup check source <table|all>",
			"sqlbackup check target <table|all>",
			"sqlbackup check both <table|all>",
			"sqlbackup repair replace <table|all>",
			"sqlbackup repair skip <table|all>",
			"sqlbackup repair error <table|all>",
			"sqlbackup repair merge-replace <table|all>",
			"sqlbackup repair merge-skip <table|all>"
		};

		private readonly bool m_enabled;
		private readonly string m_connectionString;
		private readonly string m_targetConnectionString;
		private readonly string m_commandPrefix;
		private readonly string m_backupFolder;
		private readonly string m_remoteUser;
		private readonly string m_remotePassword;
		private readonly int m_remoteTimeoutSeconds;
		private readonly long m_maxSingleTableExportBytes;
		private readonly long m_maxOtbPartBytes;
		private readonly ConflictMode m_defaultConflictMode;
		private readonly object m_tableProcessingLock = new object();
		private int m_bulkOperationRunning;

		public SqlDataBackup(IConfigSource config, IHttpServer server, string configName)
		{
			string effectiveConfigName = string.IsNullOrWhiteSpace(configName) ? "SqlDataBackup" : configName;
			IConfig moduleConfig = config.Configs[effectiveConfigName];
			if (moduleConfig == null)
				return;

			m_enabled = moduleConfig.GetBoolean("Enabled", false);
			if (!m_enabled)
				return;

			IConfig dbConfig = config.Configs["DatabaseService"];
			m_connectionString = moduleConfig.GetString("ConnectionString", dbConfig != null ? dbConfig.GetString("ConnectionString", string.Empty) : string.Empty).Trim();
			m_targetConnectionString = moduleConfig.GetString("TargetConnectionString", string.Empty).Trim();
			m_commandPrefix = moduleConfig.GetString("CommandPrefix", "sqlbackup").Trim();
			m_backupFolder = moduleConfig.GetString("BackupFolder", "backupOTB").Trim();
			m_remoteUser = moduleConfig.GetString("RemoteUser", string.Empty).Trim();
			m_remotePassword = moduleConfig.GetString("RemotePassword", string.Empty);
			m_remoteTimeoutSeconds = ReadRemoteTimeoutSeconds(moduleConfig);
			m_maxSingleTableExportBytes = ReadMaxSingleTableExportBytes(moduleConfig);
			m_maxOtbPartBytes = ReadMaxOtbPartBytes(moduleConfig);
			m_defaultConflictMode = ReadConflictMode(moduleConfig.GetString("DefaultConflictMode", "replace"), ConflictMode.Replace);

			if (string.IsNullOrWhiteSpace(m_connectionString))
			{
				m_log.Error("[SQL DATA BACKUP]: ConnectionString is missing. Add-on will not be enabled.");
				return;
			}

			if (MainConsole.Instance == null)
			{
				m_log.Warn("[SQL DATA BACKUP]: MainConsole is not available. Commands were not registered.");
				return;
			}

			RegisterCommands();

			m_log.InfoFormat(
				"[SQL DATA BACKUP] v{6}: Enabled. Command prefix '{0}', BackupFolder='{1}', TargetConnectionString={2}, DefaultConflictMode={3}, MaxSingleTableExportBytes={4}, MaxOtbPartBytes={5}.",
				m_commandPrefix,
				m_backupFolder,
				string.IsNullOrWhiteSpace(m_targetConnectionString) ? "no" : "yes",
				m_defaultConflictMode,
				m_maxSingleTableExportBytes,
				m_maxOtbPartBytes,
				ModuleVersion);
		}

		private void RegisterCommands()
		{
			MainConsole.Instance.Commands.AddCommand(
				"Backup", false,
				m_commandPrefix,
				m_commandPrefix + " help",
				"SQL data backup commands.",
				HandleHelpCommand);

			MainConsole.Instance.Commands.AddCommand("Backup", false, m_commandPrefix + " help", m_commandPrefix + " help", "Show sqlbackup command help.", HandleHelpCommand);
			MainConsole.Instance.Commands.AddCommand("Backup", false, m_commandPrefix + " list", m_commandPrefix + " list", "List all source database tables.", HandleListCommand);

			MainConsole.Instance.Commands.AddCommand("Backup", false, m_commandPrefix + " export", m_commandPrefix + " export <table>", "Export one table to a default OTB file.", HandleExportCommand);
			MainConsole.Instance.Commands.AddCommand("Backup", false, m_commandPrefix + " export", m_commandPrefix + " export <table> <datei.otb|url>", "Export one table to a specific OTB file or URL.", HandleExportCommand);
			MainConsole.Instance.Commands.AddCommand("Backup", false, m_commandPrefix + " export all", m_commandPrefix + " export all <ordner|url>", "Export all tables to a folder or URL.", HandleExportCommand);

			MainConsole.Instance.Commands.AddCommand("Backup", false, m_commandPrefix + " import replace", m_commandPrefix + " import replace <table|all> <datei.otb|ordner|url>", "Import with replace mode.", HandleImportReplaceCommand);
			MainConsole.Instance.Commands.AddCommand("Backup", false, m_commandPrefix + " import skip", m_commandPrefix + " import skip <table|all> <datei.otb|ordner|url>", "Import with skip mode.", HandleImportSkipCommand);
			MainConsole.Instance.Commands.AddCommand("Backup", false, m_commandPrefix + " import error", m_commandPrefix + " import error <table|all> <datei.otb|ordner|url>", "Import with error mode.", HandleImportErrorCommand);
			MainConsole.Instance.Commands.AddCommand("Backup", false, m_commandPrefix + " import merge-replace", m_commandPrefix + " import merge-replace <table|all> <datei.otb|ordner|url>", "Import with merge-replace mode.", HandleImportMergeReplaceCommand);
			MainConsole.Instance.Commands.AddCommand("Backup", false, m_commandPrefix + " import merge-skip", m_commandPrefix + " import merge-skip <table|all> <datei.otb|ordner|url>", "Import with merge-skip mode.", HandleImportMergeSkipCommand);

			MainConsole.Instance.Commands.AddCommand("Backup", false, m_commandPrefix + " copy replace", m_commandPrefix + " copy replace <table|all>", "Copy with replace mode.", HandleCopyReplaceCommand);
			MainConsole.Instance.Commands.AddCommand("Backup", false, m_commandPrefix + " copy skip", m_commandPrefix + " copy skip <table|all>", "Copy with skip mode.", HandleCopySkipCommand);
			MainConsole.Instance.Commands.AddCommand("Backup", false, m_commandPrefix + " copy error", m_commandPrefix + " copy error <table|all>", "Copy with error mode.", HandleCopyErrorCommand);
			MainConsole.Instance.Commands.AddCommand("Backup", false, m_commandPrefix + " copy merge-replace", m_commandPrefix + " copy merge-replace <table|all>", "Copy with merge-replace mode.", HandleCopyMergeReplaceCommand);
			MainConsole.Instance.Commands.AddCommand("Backup", false, m_commandPrefix + " copy merge-skip", m_commandPrefix + " copy merge-skip <table|all>", "Copy with merge-skip mode.", HandleCopyMergeSkipCommand);

			MainConsole.Instance.Commands.AddCommand("Backup", false, m_commandPrefix + " compare", m_commandPrefix + " compare <table|all>", "Compare source and target tables.", HandleCompareCommand);

			MainConsole.Instance.Commands.AddCommand("Backup", false, m_commandPrefix + " check source", m_commandPrefix + " check source <table|all>", "Run CHECK TABLE on source.", HandleCheckSourceCommand);
			MainConsole.Instance.Commands.AddCommand("Backup", false, m_commandPrefix + " check target", m_commandPrefix + " check target <table|all>", "Run CHECK TABLE on target.", HandleCheckTargetCommand);
			MainConsole.Instance.Commands.AddCommand("Backup", false, m_commandPrefix + " check both", m_commandPrefix + " check both <table|all>", "Run CHECK TABLE on source and target.", HandleCheckBothCommand);

			MainConsole.Instance.Commands.AddCommand("Backup", false, m_commandPrefix + " repair replace", m_commandPrefix + " repair replace <table|all>", "Repair with replace mode.", HandleRepairReplaceCommand);
			MainConsole.Instance.Commands.AddCommand("Backup", false, m_commandPrefix + " repair skip", m_commandPrefix + " repair skip <table|all>", "Repair with skip mode.", HandleRepairSkipCommand);
			MainConsole.Instance.Commands.AddCommand("Backup", false, m_commandPrefix + " repair error", m_commandPrefix + " repair error <table|all>", "Repair with error mode.", HandleRepairErrorCommand);
			MainConsole.Instance.Commands.AddCommand("Backup", false, m_commandPrefix + " repair merge-replace", m_commandPrefix + " repair merge-replace <table|all>", "Repair with merge-replace mode.", HandleRepairMergeReplaceCommand);
			MainConsole.Instance.Commands.AddCommand("Backup", false, m_commandPrefix + " repair merge-skip", m_commandPrefix + " repair merge-skip <table|all>", "Repair with merge-skip mode.", HandleRepairMergeSkipCommand);
		}
		private void helptxt()
		{
			MainConsole.Instance.Output("SQL Data Backup commands:");
			for (int i = 0; i < s_helpTextLines.Length; i++)
				MainConsole.Instance.Output(s_helpTextLines[i]);
		}

		private static long ReadMaxSingleTableExportBytes(IConfig moduleConfig)
		{
			string configured = moduleConfig.GetString("MaxSingleTableExportBytes", DefaultMaxSingleTableExportBytes.ToString(CultureInfo.InvariantCulture));
			long parsed;
			if (long.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed >= 0)
				return parsed;

			return DefaultMaxSingleTableExportBytes;
		}

		private static long ReadMaxOtbPartBytes(IConfig moduleConfig)
		{
			string configured = moduleConfig.GetString("MaxOtbPartBytes", DefaultMaxOtbPartBytes.ToString(CultureInfo.InvariantCulture));
			long parsed;
			if (long.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0)
			{
				if (parsed > int.MaxValue)
					return int.MaxValue;

				return parsed;
			}

			return DefaultMaxOtbPartBytes;
		}

		private static int ReadRemoteTimeoutSeconds(IConfig moduleConfig)
		{
			string configured = moduleConfig.GetString("RemoteTimeoutSeconds", DefaultRemoteTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
			int parsed;
			if (int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0)
				return parsed;

			return DefaultRemoteTimeoutSeconds;
		}

		private static ConflictMode ReadConflictMode(string configured, ConflictMode fallback)
		{
			if (string.IsNullOrWhiteSpace(configured))
				return fallback;

			switch (configured.Trim().ToLowerInvariant())
			{
				case "replace":
					return ConflictMode.Replace;
				case "skip":
					return ConflictMode.Skip;
				case "error":
					return ConflictMode.Error;
				case "merge-replace":
					return ConflictMode.MergeReplace;
				case "merge-skip":
					return ConflictMode.MergeSkip;
				default:
					return fallback;
			}
		}

		private void HandleHelpCommand(string module, string[] cmd)
		{
			ExecuteConsoleCommand(ShowUsage);
		}

		private void HandleListCommand(string module, string[] cmd)
		{
			ExecuteConsoleCommand(ListTables);
		}

		private void HandleExportCommand(string module, string[] cmd)
		{
			ExecuteConsoleCommand(delegate { HandleExport(cmd); });
		}

		private void HandleImportReplaceCommand(string module, string[] cmd)
		{
			ExecuteConsoleCommand(delegate { HandleImport(cmd, ConflictMode.Replace); });
		}

		private void HandleImportSkipCommand(string module, string[] cmd)
		{
			ExecuteConsoleCommand(delegate { HandleImport(cmd, ConflictMode.Skip); });
		}

		private void HandleImportErrorCommand(string module, string[] cmd)
		{
			ExecuteConsoleCommand(delegate { HandleImport(cmd, ConflictMode.Error); });
		}

		private void HandleImportMergeReplaceCommand(string module, string[] cmd)
		{
			ExecuteConsoleCommand(delegate { HandleImport(cmd, ConflictMode.MergeReplace); });
		}

		private void HandleImportMergeSkipCommand(string module, string[] cmd)
		{
			ExecuteConsoleCommand(delegate { HandleImport(cmd, ConflictMode.MergeSkip); });
		}

		private void HandleCopyReplaceCommand(string module, string[] cmd)
		{
			ExecuteConsoleCommand(delegate { HandleCopy(cmd, ConflictMode.Replace); });
		}

		private void HandleCopySkipCommand(string module, string[] cmd)
		{
			ExecuteConsoleCommand(delegate { HandleCopy(cmd, ConflictMode.Skip); });
		}

		private void HandleCopyErrorCommand(string module, string[] cmd)
		{
			ExecuteConsoleCommand(delegate { HandleCopy(cmd, ConflictMode.Error); });
		}

		private void HandleCopyMergeReplaceCommand(string module, string[] cmd)
		{
			ExecuteConsoleCommand(delegate { HandleCopy(cmd, ConflictMode.MergeReplace); });
		}

		private void HandleCopyMergeSkipCommand(string module, string[] cmd)
		{
			ExecuteConsoleCommand(delegate { HandleCopy(cmd, ConflictMode.MergeSkip); });
		}

		private void HandleCompareCommand(string module, string[] cmd)
		{
			ExecuteConsoleCommand(delegate { HandleCompare(cmd); });
		}

		private void HandleCheckSourceCommand(string module, string[] cmd)
		{
			ExecuteConsoleCommand(delegate { HandleCheck(cmd, CheckScope.Source, "source"); });
		}

		private void HandleCheckTargetCommand(string module, string[] cmd)
		{
			ExecuteConsoleCommand(delegate { HandleCheck(cmd, CheckScope.Target, "target"); });
		}

		private void HandleCheckBothCommand(string module, string[] cmd)
		{
			ExecuteConsoleCommand(delegate { HandleCheck(cmd, CheckScope.Both, "both"); });
		}

		private void HandleRepairReplaceCommand(string module, string[] cmd)
		{
			ExecuteConsoleCommand(delegate { HandleRepair(cmd, ConflictMode.Replace); });
		}

		private void HandleRepairSkipCommand(string module, string[] cmd)
		{
			ExecuteConsoleCommand(delegate { HandleRepair(cmd, ConflictMode.Skip); });
		}

		private void HandleRepairErrorCommand(string module, string[] cmd)
		{
			ExecuteConsoleCommand(delegate { HandleRepair(cmd, ConflictMode.Error); });
		}

		private void HandleRepairMergeReplaceCommand(string module, string[] cmd)
		{
			ExecuteConsoleCommand(delegate { HandleRepair(cmd, ConflictMode.MergeReplace); });
		}

		private void HandleRepairMergeSkipCommand(string module, string[] cmd)
		{
			ExecuteConsoleCommand(delegate { HandleRepair(cmd, ConflictMode.MergeSkip); });
		}

		private void ExecuteConsoleCommand(Action action)
		{
			try
			{
				action();
			}
			catch (Exception ex)
			{
				m_log.Error("[SQL DATA BACKUP]: Command error.", ex);
				MainConsole.Instance.Output("Error: {0}", ex.Message);
			}
		}

		private void HandleExport(string[] cmd)
		{
			if (cmd.Length < 3)
			{
				ShowUsage();
				return;
			}

			string scope = cmd[2];
			string targetPath = cmd.Length >= 4 ? cmd[3] : string.Empty;

			if (scope.Equals("all", StringComparison.OrdinalIgnoreCase))
			{
				RunBulkInBackground("export all", delegate { ExportAllTables(targetPath); });
				return;
			}

			EnsureSafeTableName(scope);
			if (string.IsNullOrWhiteSpace(targetPath))
				targetPath = BuildDefaultBackupFilePath(scope);
			else if (LooksLikeDirectoryPath(targetPath))
				targetPath = CombineStoragePath(targetPath, BuildTimestampedBackupFileName(scope));

			ExportTable(scope, targetPath, true);
		}

		private void HandleImport(string[] cmd, ConflictMode conflictMode)
		{
			if (cmd.Length < 5)
			{
				ShowUsage();
				return;
			}

			string scope = cmd[3];
			string sourcePath = cmd[4];

			if (scope.Equals("all", StringComparison.OrdinalIgnoreCase))
			{
				RunBulkInBackground("import all", delegate { ImportAllTables(sourcePath, conflictMode); });
				return;
			}

			EnsureSafeTableName(scope);
			ImportTable(scope, sourcePath, true, conflictMode);
		}

		private void HandleCopy(string[] cmd, ConflictMode conflictMode)
		{
			EnsureTargetConnectionConfigured();

			if (cmd.Length < 4)
			{
				ShowUsage();
				return;
			}

			string scope = cmd[3];

			if (scope.Equals("all", StringComparison.OrdinalIgnoreCase))
			{
				RunBulkInBackground("copy all", delegate { CopyAllTablesToTarget(conflictMode); });
				return;
			}

			EnsureSafeTableName(scope);
			CopyTableToTarget(scope, conflictMode, true);
		}

		private void HandleCompare(string[] cmd)
		{
			EnsureTargetConnectionConfigured();

			if (cmd.Length < 3)
			{
				ShowUsage();
				return;
			}

			string scope = cmd[2];
			if (scope.Equals("all", StringComparison.OrdinalIgnoreCase))
			{
				RunBulkInBackground("compare all", CompareAllTablesAgainstTarget);
				return;
			}

			EnsureSafeTableName(scope);
			CompareTableAgainstTarget(scope, true);
		}

		private void HandleCheck(string[] cmd, CheckScope checkScope, string checkScopeName)
		{
			if (cmd.Length < 4)
			{
				ShowUsage();
				return;
			}

			string scope = cmd[3];

			if (!cmd[2].Equals(checkScopeName, StringComparison.OrdinalIgnoreCase))
			{
				ShowUsage();
				return;
			}

			if ((checkScope == CheckScope.Target || checkScope == CheckScope.Both) && string.IsNullOrWhiteSpace(m_targetConnectionString))
				throw new InvalidOperationException("TargetConnectionString is required for check target/both.");

			if (scope.Equals("all", StringComparison.OrdinalIgnoreCase))
			{
				RunBulkInBackground("check all", delegate { CheckAllTables(checkScope); });
				return;
			}

			EnsureSafeTableName(scope);
			CheckTable(scope, checkScope);
		}

		private void HandleRepair(string[] cmd, ConflictMode conflictMode)
		{
			EnsureTargetConnectionConfigured();

			if (cmd.Length < 4)
			{
				ShowUsage();
				return;
			}

			string scope = cmd[3];

			if (scope.Equals("all", StringComparison.OrdinalIgnoreCase))
			{
				RunBulkInBackground("repair all", delegate { RepairAllTables(conflictMode); });
				return;
			}

			EnsureSafeTableName(scope);
			RepairTable(scope, conflictMode);
		}

		private void ListTables()
		{
			List<string> tables = GetAllTables(m_connectionString);
			MainConsole.Instance.Output("Source tables:");
			foreach (string table in tables)
				MainConsole.Instance.Output(" - " + table);
			MainConsole.Instance.Output("Total: {0}", tables.Count);
		}

		private void ExportAllTables(string folderPath)
		{
			if (string.IsNullOrWhiteSpace(folderPath))
				folderPath = m_backupFolder;

			EnsureStorageFolder(folderPath);
			string batchTimestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

			List<string> tables = GetAllTables(m_connectionString);
			int done = 0;
			int failed = 0;
			foreach (string table in tables)
			{
				try
				{
					string filePath = CombineStoragePath(folderPath, table + "_" + batchTimestamp + OtbExtension);
					MainConsole.Instance.Output("Backing up table: {0} -> {1}", table, filePath);
					ExportTable(table, filePath, false);
					done++;
				}
				catch (Exception ex)
				{
					failed++;
					m_log.Error("[SQL DATA BACKUP]: Export failed for table: " + table, ex);
					MainConsole.Instance.Output("Export error for {0}: {1}", table, ex.Message);
				}
			}

			MainConsole.Instance.Output("Export completed: {0} ok, {1} errors, target {2}", done, failed, folderPath);
		}

		private void ImportAllTables(string folderPath, ConflictMode conflictMode)
		{
			if (string.IsNullOrWhiteSpace(folderPath))
				folderPath = m_backupFolder;

			List<string> files = ListStorageFiles(folderPath, OtbExtension);
			if (files.Count == 0)
				throw new FileNotFoundException("No backup files found: " + folderPath);

			files.Sort(StringComparer.OrdinalIgnoreCase);

			int done = 0;
			int failed = 0;
			foreach (string file in files)
			{
				string table = GetTableNameFromBackupFile(file);
				if (!IsSafeTableName(table))
				{
					MainConsole.Instance.Output("Skipping unsafe file name: {0}", file);
					continue;
				}

				try
				{
					MainConsole.Instance.Output("Importing table: {0} <- {1} ({2})", table, file, conflictMode.ToString().ToLowerInvariant());
					ImportTable(table, file, false, conflictMode);
					done++;
				}
				catch (Exception ex)
				{
					failed++;
					m_log.Error("[SQL DATA BACKUP]: Import from file failed: " + file, ex);
					MainConsole.Instance.Output("Import error from {0}: {1}", file, ex.Message);
				}
			}

			MainConsole.Instance.Output("Import completed: {0} ok, {1} errors, source {2}, mode {3}", done, failed, folderPath, conflictMode.ToString().ToLowerInvariant());
		}

		private void CopyAllTablesToTarget(ConflictMode conflictMode)
		{
			List<string> tables = GetAllTables(m_connectionString);
			int done = 0;
			int failed = 0;
			foreach (string table in tables)
			{
				try
				{
					CopyTableToTarget(table, conflictMode, false);
					done++;
				}
				catch (Exception ex)
				{
					failed++;
					m_log.Error("[SQL DATA BACKUP]: Copy failed for table: " + table, ex);
					MainConsole.Instance.Output("Copy error for {0}: {1}", table, ex.Message);
				}
			}

			MainConsole.Instance.Output("Copy completed: {0} ok, {1} errors, mode {2}", done, failed, conflictMode.ToString().ToLowerInvariant());
		}

		private void CompareAllTablesAgainstTarget()
		{
			List<string> sourceTables = GetAllTables(m_connectionString);
			List<string> targetTables = GetAllTables(m_targetConnectionString);
			HashSet<string> allTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string table in sourceTables)
				allTables.Add(table);
			foreach (string table in targetTables)
				allTables.Add(table);

			List<string> sorted = new List<string>(allTables);
			sorted.Sort(StringComparer.OrdinalIgnoreCase);

			int identical = 0;
			int differing = 0;
			foreach (string table in sorted)
			{
				TableComparisonResult comparison = CompareTableAgainstTarget(table, true);
				if (comparison.IsIdentical)
					identical++;
				else
					differing++;
			}

			MainConsole.Instance.Output("Compare completed: {0} identical, {1} different.", identical, differing);
		}

		private void CheckAllTables(CheckScope checkScope)
		{
			HashSet<string> tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string table in GetAllTables(m_connectionString))
				tables.Add(table);

			if (checkScope == CheckScope.Target || checkScope == CheckScope.Both)
			{
				foreach (string table in GetAllTables(m_targetConnectionString))
					tables.Add(table);
			}

			List<string> sorted = new List<string>(tables);
			sorted.Sort(StringComparer.OrdinalIgnoreCase);
			foreach (string table in sorted)
				CheckTable(table, checkScope);
		}

		private void RepairAllTables(ConflictMode conflictMode)
		{
			HashSet<string> tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string table in GetAllTables(m_connectionString))
				tables.Add(table);
			foreach (string table in GetAllTables(m_targetConnectionString))
				tables.Add(table);

			List<string> sorted = new List<string>(tables);
			sorted.Sort(StringComparer.OrdinalIgnoreCase);

			int repaired = 0;
			int skipped = 0;
			int failed = 0;
			foreach (string table in sorted)
			{
				try
				{
					bool changed = RepairTable(table, conflictMode);
					if (changed)
						repaired++;
					else
						skipped++;
				}
				catch (Exception ex)
				{
					failed++;
					m_log.Error("[SQL DATA BACKUP]: Repair failed for table: " + table, ex);
					MainConsole.Instance.Output("Repair error for {0}: {1}", table, ex.Message);
				}
			}

			MainConsole.Instance.Output("Repair completed: {0} repaired/synced, {1} unchanged, {2} errors.", repaired, skipped, failed);
		}

		private void RunBulkInBackground(string operationName, Action operation)
		{
			if (Interlocked.CompareExchange(ref m_bulkOperationRunning, 1, 0) != 0)
			{
				MainConsole.Instance.Output("A SQL backup bulk job is already running. Please wait.");
				return;
			}

			MainConsole.Instance.Output("Starting {0} in background...", operationName);
			ThreadPool.QueueUserWorkItem(delegate
			{
				try
				{
					operation();
				}
				catch (Exception ex)
				{
					m_log.Error("[SQL DATA BACKUP]: Bulk operation failed.", ex);
					MainConsole.Instance.Output("Bulk operation failed: {0}", ex.Message);
				}
				finally
				{
					Interlocked.Exchange(ref m_bulkOperationRunning, 0);
				}
			});
		}

		private static string GetTableNameFromBackupFile(string filePath)
		{
			string fileName = GetStorageFileName(filePath);
			string name = Path.GetFileNameWithoutExtension(fileName) ?? string.Empty;
			name = Regex.Replace(name, "\\.part\\d{4}$", string.Empty, RegexOptions.IgnoreCase);
			Match m = Regex.Match(name, "^(?<table>[A-Za-z0-9_]+)_\\d{8}_\\d{6}$");
			if (m.Success)
				return m.Groups["table"].Value;

			return name;
		}

		private void ExportTable(string tableName, string filePath, bool verbose)
		{
			lock (m_tableProcessingLock)
			{
				EnsureSafeTableName(tableName);
				filePath = EnsureOtbPath(filePath);

				long approxBytes = GetApproxTableSizeBytes(tableName);
				if (m_maxSingleTableExportBytes > 0 && approxBytes > m_maxSingleTableExportBytes)
				{
					m_log.InfoFormat(
						"[SQL DATA BACKUP]: Table {0} is larger than MaxSingleTableExportBytes ({1} > {2}), split export continues.",
						tableName,
						approxBytes,
						m_maxSingleTableExportBytes);
				}

				EnsureStorageFolder(GetParentStoragePath(filePath));
				ExportTableToOtbParts(tableName, filePath, out int rowCount, out int skippedRows, out int partCount);
				long totalBackupBytes = GetWrittenBackupSizeBytes(filePath, partCount);

				if (verbose || partCount > 1)
				{
					MainConsole.Instance.Output(
						"Table saved: {0} ({1} entries, {2} skipped, {3} part(s), {4}) -> {5}",
						tableName,
						rowCount,
						skippedRows,
						partCount,
						FormatSize(totalBackupBytes),
						filePath);
				}
			}
		}

		private void ImportTable(string tableName, string filePath, bool verbose, ConflictMode conflictMode)
		{
			lock (m_tableProcessingLock)
			{
				EnsureSafeTableName(tableName);
				filePath = EnsureOtbPath(filePath);

				if (!StorageFileExists(filePath))
					throw new FileNotFoundException("Backup file not found", filePath);

				string scriptText = ReadOtbArchive(ReadStorageBytes(filePath), tableName, filePath);
				if (string.IsNullOrWhiteSpace(scriptText))
					throw new InvalidOperationException("Backup file is empty: " + filePath);

				using (MySqlConnection conn = new MySqlConnection(m_connectionString))
				{
					conn.Open();
					bool skippedTable;
					string preparedScript = PrepareScriptForImport(conn, tableName, scriptText, conflictMode, out skippedTable);
					if (skippedTable)
					{
						MainConsole.Instance.Output("Import skipped: {0} already exists, mode skip <- {1}", tableName, filePath);
						return;
					}

					ExecuteScriptLenient(conn, tableName, filePath, preparedScript, out int executedStatements, out int failedStatements);

					if (failedStatements > 0)
					{
						MainConsole.Instance.Output(
							"Import continued with errors: {0} ({1} statements ok, {2} skipped, mode {3}) <- {4}",
							tableName,
							executedStatements,
							failedStatements,
							conflictMode.ToString().ToLowerInvariant(),
							filePath);
					}
					else if (verbose)
					{
						MainConsole.Instance.Output("Imported: {0} ({1} statements, mode {2}) <- {3}", tableName, executedStatements, conflictMode.ToString().ToLowerInvariant(), filePath);
					}
				}
			}
		}

		private void CopyTableToTarget(string tableName, ConflictMode conflictMode, bool verbose)
		{
			lock (m_tableProcessingLock)
			{
				EnsureSafeTableName(tableName);

				using (MySqlConnection targetConn = new MySqlConnection(m_targetConnectionString))
				{
					targetConn.Open();
					string scriptText = BuildFullTableScript(tableName, out int rowCount, out int skippedRows);
					bool skippedTable;
					string preparedScript = PrepareScriptForImport(targetConn, tableName, scriptText, conflictMode, out skippedTable);
					if (skippedTable)
					{
						MainConsole.Instance.Output("Copy skipped: {0} already exists in target, mode skip.", tableName);
						return;
					}

					ExecuteScriptLenient(targetConn, tableName, "dbcopy:" + tableName, preparedScript, out int executedStatements, out int failedStatements);
					if (verbose || failedStatements > 0)
					{
						MainConsole.Instance.Output(
							"Copy completed: {0} ({1} rows, {2} skipped while reading, {3} statements ok, {4} errors, mode {5})",
							tableName,
							rowCount,
							skippedRows,
							executedStatements,
							failedStatements,
							conflictMode.ToString().ToLowerInvariant());
					}
				}
			}
		}

		private TableComparisonResult CompareTableAgainstTarget(string tableName, bool verbose)
		{
			EnsureSafeTableName(tableName);
			TableComparisonResult result = new TableComparisonResult();

			using (MySqlConnection sourceConn = new MySqlConnection(m_connectionString))
			using (MySqlConnection targetConn = new MySqlConnection(m_targetConnectionString))
			{
				sourceConn.Open();
				targetConn.Open();

				result.SourceExists = TableExists(sourceConn, tableName);
				result.TargetExists = TableExists(targetConn, tableName);

				if (!result.SourceExists || !result.TargetExists)
				{
					if (!result.SourceExists && !result.TargetExists)
						result.Summary = "missing in source and target";
					else if (!result.SourceExists)
						result.Summary = "missing in source";
					else
						result.Summary = "missing in target";

					if (verbose)
						MainConsole.Instance.Output("Compare {0}: {1}", tableName, result.Summary);

					return result;
				}

				string sourceCreate = NormalizeCreateStatement(GetCreateTableStatement(sourceConn, tableName));
				string targetCreate = NormalizeCreateStatement(GetCreateTableStatement(targetConn, tableName));
				result.SchemaMatches = string.Equals(sourceCreate, targetCreate, StringComparison.OrdinalIgnoreCase);
				result.SourceRowCount = GetTableRowCount(sourceConn, tableName);
				result.TargetRowCount = GetTableRowCount(targetConn, tableName);
				result.RowCountMatches = result.SourceRowCount == result.TargetRowCount;

				result.SourceChecksumAvailable = TryGetTableChecksum(sourceConn, tableName, out result.SourceChecksum);
				result.TargetChecksumAvailable = TryGetTableChecksum(targetConn, tableName, out result.TargetChecksum);
				if (result.SourceChecksumAvailable && result.TargetChecksumAvailable)
					result.ChecksumMatches = string.Equals(result.SourceChecksum, result.TargetChecksum, StringComparison.OrdinalIgnoreCase);
				else
					result.ChecksumMatches = true;

				result.Summary = string.Format(
					CultureInfo.InvariantCulture,
					"Schema={0}, Rows={1}/{2}, Checksum={3}",
					result.SchemaMatches ? "ok" : "diff",
					result.SourceRowCount,
					result.TargetRowCount,
					(result.SourceChecksumAvailable && result.TargetChecksumAvailable) ? (result.ChecksumMatches ? "ok" : "diff") : "n/a");
			}

			if (verbose)
				MainConsole.Instance.Output("Compare {0}: {1}", tableName, result.Summary);

			return result;
		}

		private void CheckTable(string tableName, CheckScope checkScope)
		{
			EnsureSafeTableName(tableName);

			if (checkScope == CheckScope.Source || checkScope == CheckScope.Both)
				RunCheckTable(m_connectionString, tableName, "source");

			if (checkScope == CheckScope.Target || checkScope == CheckScope.Both)
				RunCheckTable(m_targetConnectionString, tableName, "target");
		}

		private bool RepairTable(string tableName, ConflictMode conflictMode)
		{
			EnsureSafeTableName(tableName);
			MainConsole.Instance.Output("Repair checking {0}...", tableName);

			RunRepairTableSql(m_connectionString, tableName, "source");
			RunRepairTableSql(m_targetConnectionString, tableName, "target");

			TableComparisonResult before = CompareTableAgainstTarget(tableName, false);
			if (before.IsIdentical)
			{
				MainConsole.Instance.Output("Repair {0}: no action needed, source and target are identical.", tableName);
				return false;
			}

			CopyTableToTarget(tableName, conflictMode, false);
			TableComparisonResult after = CompareTableAgainstTarget(tableName, false);
			MainConsole.Instance.Output("Repair {0}: {1}", tableName, after.IsIdentical ? "successfully synchronized" : after.Summary);
			return true;
		}

		private void ExportTableToOtbParts(string tableName, string filePath, out int rowCount, out int skippedRows, out int partCount)
		{
			string createStatement = string.Empty;
			List<string> columns = new List<string>();
			Encoding enc = new UTF8Encoding(false);
			string createdUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
			string firstHeader = BuildPartHeader(tableName, createStatement, createdUtc, true);
			string nextHeader = BuildPartHeader(tableName, createStatement, createdUtc, false);
			const string footer = "SET FOREIGN_KEY_CHECKS=1;\n";
			long footerBytes = enc.GetByteCount(footer);
			long maxPartBytes = m_maxOtbPartBytes > 0 ? m_maxOtbPartBytes : DefaultMaxOtbPartBytes;
			string basePath = BuildSplitBasePath(filePath);
			StringBuilder currentPart = null;
			long currentBytes = 0;
			bool currentPartHasRows = false;
			bool splitMode = false;
			rowCount = 0;
			skippedRows = 0;
			partCount = 0;

			DeleteExistingSplitArtifacts(filePath, basePath);

			using (MySqlConnection conn = new MySqlConnection(m_connectionString))
			{
				conn.Open();

				createStatement = GetCreateTableStatement(conn, tableName);
				firstHeader = BuildPartHeader(tableName, createStatement, createdUtc, true);
				nextHeader = BuildPartHeader(tableName, createStatement, createdUtc, false);
				currentPart = new StringBuilder(firstHeader.Length + footer.Length + 1024);
				currentPart.Append(firstHeader);
				currentBytes = enc.GetByteCount(firstHeader);

				using (MySqlCommand selectCmd = conn.CreateCommand())
				{
					selectCmd.CommandText = "SELECT * FROM `" + tableName + "`";
					using (MySqlDataReader reader = selectCmd.ExecuteReader())
					{
						for (int i = 0; i < reader.FieldCount; i++)
							columns.Add("`" + reader.GetName(i) + "`");

						while (reader.Read())
						{
							string insertLine;
							try
							{
								insertLine = BuildInsertLine(tableName, reader, columns, "INSERT INTO") + "\n";
							}
							catch (Exception ex)
							{
								skippedRows++;
								m_log.Error("[SQL DATA BACKUP]: Export row skipped for table " + tableName, ex);
								continue;
							}

							long lineBytes = enc.GetByteCount(insertLine);

							if (currentPartHasRows && currentBytes + lineBytes + footerBytes > maxPartBytes)
							{
								splitMode = true;
								FinalizePartWrite(filePath, basePath, tableName, currentPart, footer, ++partCount, splitMode);
								MainConsole.Instance.Output("Split {0} written: {1}", partCount, BuildSplitPartPath(basePath, partCount));
								currentPart = new StringBuilder(nextHeader.Length + footer.Length + 1024);
								currentPart.Append(nextHeader);
								currentBytes = enc.GetByteCount(nextHeader);
								currentPartHasRows = false;
							}

							currentPart.Append(insertLine);
							currentBytes += lineBytes;
							currentPartHasRows = true;
							rowCount++;
						}
					}
				}
			}

			if (currentPart == null)
				throw new InvalidOperationException("Export part could not be initialized.");

			if (rowCount == 0)
			{
				FinalizePartWrite(filePath, basePath, tableName, currentPart, footer, 1, false);
				partCount = 1;
			}
			else
			{
				FinalizePartWrite(filePath, basePath, tableName, currentPart, footer, ++partCount, splitMode);
				if (splitMode)
					MainConsole.Instance.Output("Split {0} written: {1}", partCount, BuildSplitPartPath(basePath, partCount));
			}
		}

		private string BuildFullTableScript(string tableName, out int rowCount, out int skippedRows)
		{
			EnsureSafeTableName(tableName);
			StringBuilder builder = new StringBuilder(4096);
			string createdUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
			rowCount = 0;
			skippedRows = 0;

			using (MySqlConnection conn = new MySqlConnection(m_connectionString))
			{
				conn.Open();
				string createStatement = GetCreateTableStatement(conn, tableName);
				builder.Append(BuildPartHeader(tableName, createStatement, createdUtc, true));

				using (MySqlCommand cmd = conn.CreateCommand())
				{
					cmd.CommandText = "SELECT * FROM `" + tableName + "`";
					using (MySqlDataReader reader = cmd.ExecuteReader())
					{
						List<string> columns = new List<string>();
						for (int i = 0; i < reader.FieldCount; i++)
							columns.Add("`" + reader.GetName(i) + "`");

						while (reader.Read())
						{
							try
							{
								builder.AppendLine(BuildInsertLine(tableName, reader, columns, "INSERT INTO"));
								rowCount++;
							}
							catch (Exception ex)
							{
								skippedRows++;
								m_log.Error("[SQL DATA BACKUP]: Copy row skipped for table " + tableName, ex);
							}
						}
					}
				}
			}

			builder.AppendLine("SET FOREIGN_KEY_CHECKS=1;");
			return builder.ToString();
		}

		private static string BuildSplitPartPath(string basePath, int partNumber)
		{
			return basePath + ".part" + partNumber.ToString("D4", CultureInfo.InvariantCulture) + OtbExtension;
		}

		private string PrepareScriptForImport(MySqlConnection conn, string tableName, string scriptText, ConflictMode conflictMode, out bool skippedTable)
		{
			skippedTable = false;
			bool tableExists = TableExists(conn, tableName);

			switch (conflictMode)
			{
				case ConflictMode.Replace:
					return scriptText;
				case ConflictMode.Skip:
					if (tableExists)
					{
						skippedTable = true;
						return string.Empty;
					}

					return scriptText;
				case ConflictMode.Error:
					if (tableExists)
						throw new InvalidOperationException("Target table already exists: " + tableName);

					return scriptText;
				case ConflictMode.MergeReplace:
					return RewriteScriptForMerge(scriptText, tableExists, "REPLACE INTO");
				case ConflictMode.MergeSkip:
					return RewriteScriptForMerge(scriptText, tableExists, "INSERT IGNORE INTO");
				default:
					return scriptText;
			}
		}

		private static string RewriteScriptForMerge(string scriptText, bool tableExists, string insertVerb)
		{
			StringBuilder builder = new StringBuilder(scriptText.Length + 256);
			foreach (string statement in SplitSqlStatements(scriptText))
			{
				string trimmed = statement.Trim();
				if (trimmed.Length == 0)
					continue;

				string upper = trimmed.ToUpperInvariant();
				if (tableExists && (upper.StartsWith("DROP TABLE ") || upper.StartsWith("CREATE TABLE ")))
					continue;

				if (upper.StartsWith("INSERT INTO "))
					trimmed = insertVerb + trimmed.Substring("INSERT INTO".Length);

				builder.Append(trimmed);
				builder.AppendLine(";");
			}

			return builder.ToString();
		}

		private void ExecuteScriptLenient(MySqlConnection conn, string tableName, string sourceLabel, string scriptText, out int executedStatements, out int failedStatements)
		{
			executedStatements = 0;
			failedStatements = 0;

			foreach (string statement in SplitSqlStatements(scriptText))
			{
				if (string.IsNullOrWhiteSpace(statement))
					continue;

				using (MySqlCommand cmd = conn.CreateCommand())
				{
					cmd.CommandText = statement;
					try
					{
						cmd.ExecuteNonQuery();
						executedStatements++;
					}
					catch (Exception ex)
					{
						failedStatements++;
						string preview = statement.Length > 180 ? statement.Substring(0, 180) + "..." : statement;
						m_log.Error("[SQL DATA BACKUP]: Statement skipped for table " + tableName + " from " + sourceLabel + " -> " + preview, ex);
					}
				}
			}
		}

		private static IEnumerable<string> SplitSqlStatements(string scriptText)
		{
			StringBuilder sb = new StringBuilder(scriptText.Length > 4096 ? 4096 : scriptText.Length);
			bool inSingleQuote = false;
			bool inDoubleQuote = false;
			bool inBacktick = false;
			bool escaped = false;

			for (int i = 0; i < scriptText.Length; i++)
			{
				char c = scriptText[i];

				if (escaped)
				{
					sb.Append(c);
					escaped = false;
					continue;
				}

				if (c == '\\')
				{
					sb.Append(c);
					escaped = true;
					continue;
				}

				if (!inDoubleQuote && !inBacktick && c == '\'')
				{
					inSingleQuote = !inSingleQuote;
					sb.Append(c);
					continue;
				}

				if (!inSingleQuote && !inBacktick && c == '"')
				{
					inDoubleQuote = !inDoubleQuote;
					sb.Append(c);
					continue;
				}

				if (!inSingleQuote && !inDoubleQuote && c == '`')
				{
					inBacktick = !inBacktick;
					sb.Append(c);
					continue;
				}

				if (!inSingleQuote && !inDoubleQuote && !inBacktick && c == ';')
				{
					string statement = sb.ToString().Trim();
					sb.Clear();
					if (!string.IsNullOrWhiteSpace(statement))
						yield return statement;
					continue;
				}

				sb.Append(c);
			}

			string trailing = sb.ToString().Trim();
			if (!string.IsNullOrWhiteSpace(trailing))
				yield return trailing;
		}

		private static string BuildPartHeader(string tableName, string createStatement, string createdUtc, bool firstPart)
		{
			StringBuilder builder = new StringBuilder(512);
			builder.AppendLine("-- OpenSim SQL Data Backup");
			builder.AppendLine("-- Table: " + tableName);
			builder.AppendLine("-- Created UTC: " + createdUtc);
			if (!firstPart)
				builder.AppendLine("-- Continuation");
			builder.AppendLine();
			builder.AppendLine("SET FOREIGN_KEY_CHECKS=0;");
			if (firstPart)
			{
				builder.AppendLine("DROP TABLE IF EXISTS `" + tableName + "`;");
				builder.AppendLine(createStatement + ";");
			}

			return builder.ToString();
		}

		private void FinalizePartWrite(string filePath, string basePath, string tableName, StringBuilder partBuilder, string footer, int partNumber, bool splitMode)
		{
			partBuilder.Append(footer);
			string scriptText = partBuilder.ToString();
			string targetPath = splitMode ? BuildSplitPartPath(basePath, partNumber) : filePath;
			WriteSingleOtbArchive(targetPath, tableName, scriptText);
		}

		private void DeleteExistingSplitArtifacts(string filePath, string basePath)
		{
			DeleteStorageFileIfExists(filePath);

			string folderPath = GetParentStoragePath(basePath);
			string baseName = GetStorageFileName(basePath);
			List<string> existing = ListStorageFiles(folderPath, OtbExtension);
			for (int i = 0; i < existing.Count; i++)
			{
				string fileName = GetStorageFileName(existing[i]);
				if (fileName.StartsWith(baseName + ".part", StringComparison.OrdinalIgnoreCase))
					DeleteStorageFileIfExists(existing[i]);
			}
		}

		private long GetWrittenBackupSizeBytes(string filePath, int partCount)
		{
			if (partCount <= 1)
				return GetStorageFileSizeBytes(filePath);

			string basePath = BuildSplitBasePath(filePath);
			long total = 0;
			for (int i = 1; i <= partCount; i++)
				total += GetStorageFileSizeBytes(BuildSplitPartPath(basePath, i));

			return total;
		}

		private static string FormatSize(long bytes)
		{
			string[] units = new string[] { "B", "KB", "MB", "GB", "TB" };
			double value = bytes;
			int unitIndex = 0;
			while (value >= 1024 && unitIndex < units.Length - 1)
			{
				value /= 1024;
				unitIndex++;
			}

			return value.ToString("0.##", CultureInfo.InvariantCulture) + " " + units[unitIndex];
		}

		private void WriteSingleOtbArchive(string filePath, string tableName, string scriptText)
		{
			byte[] archiveData = BuildSingleOtbArchive(tableName, scriptText);
			WriteStorageBytes(filePath, archiveData);
		}

		private static byte[] BuildSingleOtbArchive(string tableName, string scriptText)
		{
			byte[] sqlData = new UTF8Encoding(false).GetBytes(scriptText);
			using (MemoryStream memoryStream = new MemoryStream())
			{
				using (GZipStream gzip = new GZipStream(memoryStream, CompressionMode.Compress, true))
				{
					TarArchiveWriter tar = new TarArchiveWriter(gzip);
					tar.WriteFile(tableName + ".sql", sqlData);
					tar.Close();
				}

				return memoryStream.ToArray();
			}
		}

		private static string ReadOtbArchive(byte[] archiveData, string expectedTableName, string sourceLabel)
		{
			using (MemoryStream memoryStream = new MemoryStream(archiveData, false))
			using (GZipStream gzip = new GZipStream(memoryStream, CompressionMode.Decompress))
			{
				TarArchiveReader tar = new TarArchiveReader(gzip);
				while (true)
				{
					string entryPath;
					TarArchiveReader.TarEntryType entryType;
					byte[] data = tar.ReadEntry(out entryPath, out entryType);
					if (data == null)
						break;

					if (entryType != TarArchiveReader.TarEntryType.TYPE_NORMAL_FILE)
						continue;

					string fileName = Path.GetFileName(entryPath);
					if (string.IsNullOrWhiteSpace(fileName))
						continue;

					string tableFromEntry = Path.GetFileNameWithoutExtension(fileName);
					if (!tableFromEntry.Equals(expectedTableName, StringComparison.OrdinalIgnoreCase))
						continue;

					return Encoding.UTF8.GetString(data);
				}
			}

			throw new InvalidOperationException("No valid SQL payload for table found in archive: " + sourceLabel);
		}

		private static string EnsureOtbPath(string path)
		{
			if (path.EndsWith(OtbExtension, StringComparison.OrdinalIgnoreCase))
				return path;

			return path + OtbExtension;
		}

		private string BuildDefaultBackupFilePath(string tableName)
		{
			EnsureStorageFolder(m_backupFolder);
			return CombineStoragePath(m_backupFolder, BuildTimestampedBackupFileName(tableName));
		}

		private static string BuildTimestampedBackupFileName(string tableName)
		{
			string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
			return tableName + "_" + timestamp + OtbExtension;
		}

		private List<string> GetAllTables(string connectionString)
		{
			List<string> tables = new List<string>();

			using (MySqlConnection conn = new MySqlConnection(connectionString))
			{
				conn.Open();

				using (MySqlCommand cmd = conn.CreateCommand())
				{
					cmd.CommandText = "SHOW TABLES";
					using (MySqlDataReader reader = cmd.ExecuteReader())
					{
						while (reader.Read())
						{
							string table = reader.GetString(0);
							if (IsSafeTableName(table))
								tables.Add(table);
						}
					}
				}
			}

			tables.Sort(StringComparer.OrdinalIgnoreCase);
			return tables;
		}

		private long GetApproxTableSizeBytes(string tableName)
		{
			using (MySqlConnection conn = new MySqlConnection(m_connectionString))
			{
				conn.Open();

				using (MySqlCommand cmd = conn.CreateCommand())
				{
					cmd.CommandText = "SELECT COALESCE(DATA_LENGTH,0) + COALESCE(INDEX_LENGTH,0) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table";
					cmd.Parameters.AddWithValue("@table", tableName);

					object result = cmd.ExecuteScalar();
					if (result == null || result == DBNull.Value)
						return 0;

					return Convert.ToInt64(result, CultureInfo.InvariantCulture);
				}
			}
		}

		private static string BuildInsertLine(string tableName, IDataRecord row, List<string> columnNames, string insertVerb)
		{
			StringBuilder sb = new StringBuilder(256);
			sb.Append(insertVerb);
			sb.Append(" `");
			sb.Append(tableName);
			sb.Append("` (");
			sb.Append(string.Join(",", columnNames.ToArray()));
			sb.Append(") VALUES (");

			for (int i = 0; i < row.FieldCount; i++)
			{
				if (i > 0)
					sb.Append(',');

				sb.Append(ToSqlLiteral(row.GetValue(i)));
			}

			sb.Append(");");
			return sb.ToString();
		}

		private static string ToSqlLiteral(object value)
		{
			if (value == null || value == DBNull.Value)
				return "NULL";

			Type t = value.GetType();

			if (t == typeof(byte[]))
			{
				byte[] data = (byte[])value;
				StringBuilder hex = new StringBuilder(data.Length * 2 + 2);
				hex.Append("0x");
				for (int i = 0; i < data.Length; i++)
					hex.Append(data[i].ToString("X2", CultureInfo.InvariantCulture));
				return hex.ToString();
			}

			if (t == typeof(bool))
				return (bool)value ? "1" : "0";

			if (t == typeof(DateTime))
			{
				DateTime dt = (DateTime)value;
				return "'" + dt.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "'";
			}

			if (t == typeof(float) || t == typeof(double) || t == typeof(decimal))
				return Convert.ToString(value, CultureInfo.InvariantCulture);

			if (t == typeof(sbyte) || t == typeof(byte) || t == typeof(short) || t == typeof(ushort) ||
				t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong))
				return Convert.ToString(value, CultureInfo.InvariantCulture);

			string s = Convert.ToString(value, CultureInfo.InvariantCulture);
			s = MySqlHelper.EscapeString(s ?? string.Empty);
			return "'" + s + "'";
		}

		private static bool IsSafeTableName(string tableName)
		{
			if (string.IsNullOrWhiteSpace(tableName))
				return false;

			return s_safeTableNameRegex.IsMatch(tableName);
		}

		private static void EnsureSafeTableName(string tableName)
		{
			if (!IsSafeTableName(tableName))
				throw new InvalidOperationException("Unsafe table name: " + tableName);
		}

		private void EnsureTargetConnectionConfigured()
		{
			if (string.IsNullOrWhiteSpace(m_targetConnectionString))
				throw new InvalidOperationException("TargetConnectionString is missing. This function requires a target database.");
		}

		private static string NormalizeCreateStatement(string createStatement)
		{
			if (string.IsNullOrWhiteSpace(createStatement))
				return string.Empty;

			string normalized = s_autoIncrementRegex.Replace(createStatement, "AUTO_INCREMENT=0");
			normalized = Regex.Replace(normalized, "\\s+", " ").Trim();
			return normalized;
		}

		private static string GetCreateTableStatement(MySqlConnection conn, string tableName)
		{
			using (MySqlCommand createCmd = conn.CreateCommand())
			{
				createCmd.CommandText = "SHOW CREATE TABLE `" + tableName + "`";
				using (MySqlDataReader reader = createCmd.ExecuteReader())
				{
					if (!reader.Read())
						throw new InvalidOperationException("Table not found: " + tableName);

					return reader.GetString("Create Table");
				}
			}
		}

		private static bool TableExists(MySqlConnection conn, string tableName)
		{
			using (MySqlCommand cmd = conn.CreateCommand())
			{
				cmd.CommandText = "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table";
				cmd.Parameters.AddWithValue("@table", tableName);
				object result = cmd.ExecuteScalar();
				return Convert.ToInt64(result, CultureInfo.InvariantCulture) > 0;
			}
		}

		private static long GetTableRowCount(MySqlConnection conn, string tableName)
		{
			using (MySqlCommand cmd = conn.CreateCommand())
			{
				cmd.CommandText = "SELECT COUNT(*) FROM `" + tableName + "`";
				object result = cmd.ExecuteScalar();
				return Convert.ToInt64(result, CultureInfo.InvariantCulture);
			}
		}

		private static bool TryGetTableChecksum(MySqlConnection conn, string tableName, out string checksum)
		{
			checksum = string.Empty;
			try
			{
				using (MySqlCommand cmd = conn.CreateCommand())
				{
					cmd.CommandText = "CHECKSUM TABLE `" + tableName + "`";
					using (MySqlDataReader reader = cmd.ExecuteReader())
					{
						if (!reader.Read())
							return false;

						if (reader.IsDBNull(1))
							return false;

						checksum = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture);
						return !string.IsNullOrWhiteSpace(checksum);
					}
				}
			}
			catch (Exception ex)
			{
				m_log.Warn("[SQL DATA BACKUP]: CHECKSUM TABLE is not available for " + tableName + ".", ex);
				return false;
			}
		}

		private void RunCheckTable(string connectionString, string tableName, string label)
		{
			using (MySqlConnection conn = new MySqlConnection(connectionString))
			{
				conn.Open();
				if (!TableExists(conn, tableName))
				{
					MainConsole.Instance.Output("Check {0} {1}: table is missing.", label, tableName);
					return;
				}

				using (MySqlCommand cmd = conn.CreateCommand())
				{
					cmd.CommandText = "CHECK TABLE `" + tableName + "`";
					using (MySqlDataReader reader = cmd.ExecuteReader())
					{
						while (reader.Read())
						{
							string msgType = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
							string msgText = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
							MainConsole.Instance.Output("Check {0} {1}: {2} - {3}", label, tableName, msgType, msgText);
						}
					}
				}
			}
		}

		private void RunRepairTableSql(string connectionString, string tableName, string label)
		{
			using (MySqlConnection conn = new MySqlConnection(connectionString))
			{
				conn.Open();
				if (!TableExists(conn, tableName))
				{
					MainConsole.Instance.Output("Repair {0} {1}: table is missing.", label, tableName);
					return;
				}

				try
				{
					using (MySqlCommand cmd = conn.CreateCommand())
					{
						cmd.CommandText = "REPAIR TABLE `" + tableName + "`";
						using (MySqlDataReader reader = cmd.ExecuteReader())
						{
							while (reader.Read())
							{
								string msgType = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
								string msgText = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
								MainConsole.Instance.Output("Repair {0} {1}: {2} - {3}", label, tableName, msgType, msgText);
							}
						}
					}
				}
				catch (Exception ex)
				{
					m_log.Warn("[SQL DATA BACKUP]: REPAIR TABLE is not available for " + label + " " + tableName + ".", ex);
					MainConsole.Instance.Output("Repair {0} {1}: not available ({2})", label, tableName, ex.Message);
				}
			}
		}

		private string BuildSplitBasePath(string filePath)
		{
			string parent = GetParentStoragePath(filePath);
			string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(GetStorageFileName(filePath));
			return CombineStoragePathStatic(parent, fileNameWithoutExtension);
		}

		private bool LooksLikeDirectoryPath(string path)
		{
			if (string.IsNullOrWhiteSpace(path))
				return false;

			if (IsRemotePath(path))
				return path.EndsWith("/", StringComparison.Ordinal) || !path.EndsWith(OtbExtension, StringComparison.OrdinalIgnoreCase);

			if (Directory.Exists(path))
				return true;

			char last = path[path.Length - 1];
			return last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar;
		}

		private static bool IsRemotePath(string path)
		{
			Uri uri;
			if (Uri.TryCreate(path, UriKind.Absolute, out uri))
				return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

			return false;
		}

		private void EnsureStorageFolder(string path)
		{
			if (string.IsNullOrWhiteSpace(path))
				return;

			if (IsRemotePath(path))
			{
				EnsureRemoteFolder(path);
				return;
			}

			Directory.CreateDirectory(path);
		}

		private bool StorageFileExists(string path)
		{
			if (IsRemotePath(path))
				return RemoteFileExists(path);

			return File.Exists(path);
		}

		private long GetStorageFileSizeBytes(string path)
		{
			if (IsRemotePath(path))
				return GetRemoteFileSizeBytes(path);

			FileInfo info = new FileInfo(path);
			return info.Exists ? info.Length : 0;
		}

		private void DeleteStorageFileIfExists(string path)
		{
			if (IsRemotePath(path))
			{
				if (RemoteFileExists(path))
					DeleteRemoteFile(path);

				return;
			}

			if (File.Exists(path))
			{
				File.SetAttributes(path, FileAttributes.Normal);
				File.Delete(path);
			}
		}

		private byte[] ReadStorageBytes(string path)
		{
			if (IsRemotePath(path))
				return ReadRemoteBytes(path);

			return File.ReadAllBytes(path);
		}

		private void WriteStorageBytes(string path, byte[] data)
		{
			if (IsRemotePath(path))
			{
				WriteRemoteBytes(path, data);
				return;
			}

			string parent = Path.GetDirectoryName(Path.GetFullPath(path));
			if (!string.IsNullOrWhiteSpace(parent))
				Directory.CreateDirectory(parent);

			if (File.Exists(path))
			{
				File.SetAttributes(path, FileAttributes.Normal);
				File.Delete(path);
			}

			File.WriteAllBytes(path, data);
		}

		private List<string> ListStorageFiles(string folderPath, string extension)
		{
			List<string> files = new List<string>();
			if (IsRemotePath(folderPath))
			{
				files.AddRange(ListRemoteFiles(folderPath, extension));
				return files;
			}

			if (!Directory.Exists(folderPath))
				throw new DirectoryNotFoundException("Folder not found: " + folderPath);

			string[] localFiles = Directory.GetFiles(folderPath, "*" + extension);
			for (int i = 0; i < localFiles.Length; i++)
				files.Add(localFiles[i]);

			return files;
		}

		private string GetParentStoragePath(string path)
		{
			if (string.IsNullOrWhiteSpace(path))
				return string.Empty;

			if (IsRemotePath(path))
			{
				Uri uri = new Uri(path, UriKind.Absolute);
				string absolutePath = uri.AbsolutePath.TrimEnd('/');
				int slashIndex = absolutePath.LastIndexOf('/');
				string parentPath = slashIndex > 0 ? absolutePath.Substring(0, slashIndex + 1) : "/";
				UriBuilder builder = new UriBuilder(uri.Scheme, uri.Host, uri.Port, parentPath);
				return builder.Uri.AbsoluteUri;
			}

			string parent = Path.GetDirectoryName(path);
			return parent ?? string.Empty;
		}

		private string CombineStoragePath(string basePath, string childName)
		{
			return CombineStoragePathStatic(basePath, childName);
		}

		private static string CombineStoragePathStatic(string basePath, string childName)
		{
			if (string.IsNullOrWhiteSpace(basePath))
				return childName;

			if (IsRemotePath(basePath))
			{
				string normalized = basePath.EndsWith("/", StringComparison.Ordinal) ? basePath : basePath + "/";
				Uri baseUri = new Uri(normalized, UriKind.Absolute);
				Uri combined = new Uri(baseUri, Uri.EscapeDataString(childName));
				return combined.AbsoluteUri;
			}

			return Path.Combine(basePath, childName);
		}

		private static string GetStorageFileName(string path)
		{
			if (string.IsNullOrWhiteSpace(path))
				return string.Empty;

			if (IsRemotePath(path))
			{
				Uri uri = new Uri(path, UriKind.Absolute);
				return Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
			}

			return Path.GetFileName(path);
		}

		private void EnsureRemoteFolder(string folderUrl)
		{
			Uri uri = new Uri(folderUrl.EndsWith("/", StringComparison.Ordinal) ? folderUrl : folderUrl + "/", UriKind.Absolute);
			string[] segments = uri.AbsolutePath.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
			StringBuilder currentPath = new StringBuilder("/");

			for (int i = 0; i < segments.Length; i++)
			{
				currentPath.Append(Uri.EscapeDataString(Uri.UnescapeDataString(segments[i])));
				currentPath.Append('/');

				UriBuilder builder = new UriBuilder(uri.Scheme, uri.Host, uri.Port, currentPath.ToString());
				CreateRemoteCollection(builder.Uri.AbsoluteUri);
			}
		}

		private void CreateRemoteCollection(string folderUrl)
		{
			try
			{
				HttpWebRequest request = CreateRemoteRequest(folderUrl, "MKCOL");
				using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
				{
				}
			}
			catch (WebException ex)
			{
				HttpWebResponse response = ex.Response as HttpWebResponse;
				if (response != null)
				{
					if (response.StatusCode == HttpStatusCode.MethodNotAllowed || response.StatusCode == HttpStatusCode.Conflict)
						return;

					if ((int)response.StatusCode == 405)
						return;
				}

				throw;
			}
		}

		private bool RemoteFileExists(string path)
		{
			try
			{
				HttpWebRequest request = CreateRemoteRequest(path, "HEAD");
				using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
				{
					return response.StatusCode == HttpStatusCode.OK;
				}
			}
			catch (WebException ex)
			{
				HttpWebResponse response = ex.Response as HttpWebResponse;
				if (response != null && response.StatusCode == HttpStatusCode.NotFound)
					return false;

				throw;
			}
		}

		private long GetRemoteFileSizeBytes(string path)
		{
			try
			{
				HttpWebRequest request = CreateRemoteRequest(path, "HEAD");
				using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
				{
					return response.ContentLength > 0 ? response.ContentLength : 0;
				}
			}
			catch (WebException ex)
			{
				m_log.Warn("[SQL DATA BACKUP]: Remote file size could not be read: " + path, ex);
				return 0;
			}
		}

		private byte[] ReadRemoteBytes(string path)
		{
			HttpWebRequest request = CreateRemoteRequest(path, "GET");
			using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
			using (Stream responseStream = response.GetResponseStream())
			using (MemoryStream memory = new MemoryStream())
			{
				responseStream.CopyTo(memory);
				return memory.ToArray();
			}
		}

		private void WriteRemoteBytes(string path, byte[] data)
		{
			EnsureRemoteFolder(GetParentStoragePath(path));
			HttpWebRequest request = CreateRemoteRequest(path, "PUT");
			request.ContentLength = data.Length;
			using (Stream requestStream = request.GetRequestStream())
			{
				requestStream.Write(data, 0, data.Length);
			}

			using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
			{
			}
		}

		private void DeleteRemoteFile(string path)
		{
			try
			{
				HttpWebRequest request = CreateRemoteRequest(path, "DELETE");
				using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
				{
				}
			}
			catch (WebException ex)
			{
				HttpWebResponse response = ex.Response as HttpWebResponse;
				if (response != null && response.StatusCode == HttpStatusCode.NotFound)
					return;

				throw;
			}
		}

		private List<string> ListRemoteFiles(string folderUrl, string extension)
		{
			EnsureRemoteFolder(folderUrl);
			HttpWebRequest request = CreateRemoteRequest(folderUrl.EndsWith("/", StringComparison.Ordinal) ? folderUrl : folderUrl + "/", "PROPFIND");
			request.Headers.Add("Depth", "1");
			byte[] body = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\" ?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype /><d:getcontentlength /></d:prop></d:propfind>");
			request.ContentType = "text/xml";
			request.ContentLength = body.Length;
			using (Stream requestStream = request.GetRequestStream())
			{
				requestStream.Write(body, 0, body.Length);
			}

			List<string> files = new List<string>();
			Uri folderUri = new Uri(folderUrl.EndsWith("/", StringComparison.Ordinal) ? folderUrl : folderUrl + "/", UriKind.Absolute);
			using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
			using (Stream responseStream = response.GetResponseStream())
			{
				XmlDocument document = new XmlDocument();
				document.Load(responseStream);
				XmlNamespaceManager ns = new XmlNamespaceManager(document.NameTable);
				ns.AddNamespace("d", "DAV:");

				XmlNodeList responses = document.SelectNodes("//d:response", ns);
				for (int i = 0; i < responses.Count; i++)
				{
					XmlNode hrefNode = responses[i].SelectSingleNode("d:href", ns);
					XmlNode collectionNode = responses[i].SelectSingleNode("d:propstat/d:prop/d:resourcetype/d:collection", ns);
					if (hrefNode == null || collectionNode != null)
						continue;

					Uri entryUri = new Uri(folderUri, hrefNode.InnerText);
					if (string.Equals(entryUri.AbsoluteUri.TrimEnd('/'), folderUri.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
						continue;

					string fileName = Uri.UnescapeDataString(Path.GetFileName(entryUri.AbsolutePath));
					if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
						files.Add(entryUri.AbsoluteUri);
				}
			}

			return files;
		}

		private HttpWebRequest CreateRemoteRequest(string url, string method)
		{
			HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
			request.Method = method;
			request.Timeout = m_remoteTimeoutSeconds * 1000;
			request.ReadWriteTimeout = m_remoteTimeoutSeconds * 1000;
			request.AllowAutoRedirect = true;
			request.UserAgent = "OpenSim.SqlDataBackup";
			ApplyRemoteCredentials(request);
			return request;
		}

		private void ApplyRemoteCredentials(HttpWebRequest request)
		{
			if (string.IsNullOrWhiteSpace(m_remoteUser))
				return;

			request.PreAuthenticate = true;
			request.Credentials = new NetworkCredential(m_remoteUser, m_remotePassword ?? string.Empty);
			string auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(m_remoteUser + ":" + (m_remotePassword ?? string.Empty)));
			request.Headers[HttpRequestHeader.Authorization] = "Basic " + auth;
		}

		private void ShowUsage()
		{
			helptxt();
		}
	}
}
