/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using log4net;
using MySql.Data.MySqlClient;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Data;

namespace OpenSim.Data.MySQL
{
	/// <summary>
	/// MySQL storage provider that stores assets in type-specific tables:
	/// assets_{assetType}. A legacy fallback to table "assets" can be enabled explicitly.
	/// </summary>
	public class MySQLtsAssetData : AssetDataBase, ITSAssetAdminData
	{
		private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

		private const string LegacyTableName = "assets";
		private const string IndexTableName = "tsassets_index";
		private const string MoveCheckpointTableName = "tsassets_move_checkpoint";
		private const string TypedTableNameFormat = "assets_{0}";
		private const bool DefaultFallbackToLegacy = false;
		private const int DefaultTsAdminBatchSize = 1000;
		private const int DefaultTsAdminCommandTimeoutSeconds = 300;

		private readonly object m_tableSync = new object();
		private readonly HashSet<string> m_initializedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		private string m_connectionString;
		private bool m_fallbackToLegacy;
		private int m_tsAdminBatchSize = DefaultTsAdminBatchSize;
		private int m_tsAdminCommandTimeoutSeconds = DefaultTsAdminCommandTimeoutSeconds;

		protected virtual Assembly Assembly
		{
			get { return GetType().Assembly; }
		}

		#region IPlugin Members

		public override string Version
		{
			get { return "1.0.0.0"; }
		}

		public override string Name
		{
			get { return "MySQL TSAsset storage engine"; }
		}

		public override void Initialise(string connect)
		{
			if (string.IsNullOrEmpty(connect))
				throw new ArgumentException("Connection string must not be null or empty", nameof(connect));

			m_connectionString = connect;
			m_fallbackToLegacy = TryReadBooleanSetting(connect, "TSFallbackToLegacyAssets", DefaultFallbackToLegacy);
			m_tsAdminBatchSize = Math.Max(1, TryReadIntSetting(connect, "TSAdminBatchSize", DefaultTsAdminBatchSize));
			m_tsAdminCommandTimeoutSeconds = Math.Max(30, TryReadIntSetting(connect, "TSAdminCommandTimeoutSeconds", DefaultTsAdminCommandTimeoutSeconds));

			using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
			{
				dbcon.Open();

				Migration migration = new Migration(dbcon, Assembly, "TSAssetStore");
				migration.Update();

				EnsureIndexTable(dbcon);
				EnsureMoveCheckpointTable(dbcon);
			}
		}

		public override void Initialise()
		{
			throw new NotImplementedException();
		}

		public override void Dispose()
		{
		}

		#endregion

		#region IAssetDataPlugin Members

		public override AssetBase GetAsset(UUID assetID)
		{
			try
			{
				using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
				{
					dbcon.Open();

					if (TryGetIndexedAssetType(dbcon, assetID, out sbyte indexedType))
					{
						string typedTable = GetTypeTableName(indexedType);

						AssetBase typedAsset = GetAssetFromTable(dbcon, typedTable, assetID);
						if (typedAsset != null)
							return typedAsset;

						RemoveIndexRow(dbcon, assetID);
					}

					if (m_fallbackToLegacy)
					{
						AssetBase legacyAsset = GetAssetFromTable(dbcon, LegacyTableName, assetID);
						if (legacyAsset != null)
						{
							TryStoreTypedWithExistingConnection(dbcon, legacyAsset);
							return legacyAsset;
						}
					}
				}
			}
			catch (Exception e)
			{
				m_log.ErrorFormat("[TSASSET DB]: MySQL failure fetching asset {0}. Error: {1}", assetID, e.Message);
			}

			return null;
		}

		public override bool StoreAsset(AssetBase asset)
		{
			if (asset == null)
				return false;

			try
			{
				using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
				{
					dbcon.Open();
					return TryStoreTypedWithExistingConnection(dbcon, asset);
				}
			}
			catch (Exception e)
			{
				m_log.ErrorFormat("[TSASSET DB]: MySQL failure storing asset {0}. Error: {1}", asset.FullID, e.Message);
				return false;
			}
		}

		public override bool[] AssetsExist(UUID[] uuids)
		{
			if (uuids == null || uuids.Length == 0)
				return new bool[0];

			bool[] results = new bool[uuids.Length];
			Dictionary<UUID, List<int>> positions = new Dictionary<UUID, List<int>>();

			for (int i = 0; i < uuids.Length; i++)
			{
				if (!positions.TryGetValue(uuids[i], out List<int> indexes))
				{
					indexes = new List<int>();
					positions[uuids[i]] = indexes;
				}

				indexes.Add(i);
			}

			try
			{
				using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
				{
					dbcon.Open();

					HashSet<UUID> found = QueryExistingIds(dbcon, IndexTableName, "id", uuids);

					if (m_fallbackToLegacy)
					{
						UUID[] missing = BuildMissingArray(uuids, found);
						if (missing.Length > 0)
						{
							HashSet<UUID> legacyFound = QueryExistingIds(dbcon, LegacyTableName, "id", missing);
							foreach (UUID id in legacyFound)
								found.Add(id);
						}
					}

					foreach (UUID id in found)
					{
						if (positions.TryGetValue(id, out List<int> idxList))
						{
							foreach (int idx in idxList)
								results[idx] = true;
						}
					}
				}
			}
			catch (Exception e)
			{
				m_log.ErrorFormat("[TSASSET DB]: MySQL failure checking asset existence. Error: {0}", e.Message);
			}

			return results;
		}

		public override List<AssetMetadata> FetchAssetMetadataSet(int start, int count)
		{
			List<AssetMetadata> result = new List<AssetMetadata>(Math.Max(0, count));
			List<KeyValuePair<UUID, sbyte>> pending = new List<KeyValuePair<UUID, sbyte>>(Math.Max(0, count));

			if (count <= 0)
				return result;

			try
			{
				using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
				{
					dbcon.Open();

					using (MySqlCommand cmd = new MySqlCommand(
						$"SELECT id, assetType FROM `{IndexTableName}` ORDER BY updated_at DESC LIMIT ?start, ?count",
						dbcon))
					{
						cmd.Parameters.AddWithValue("?start", start);
						cmd.Parameters.AddWithValue("?count", count);

						using (MySqlDataReader dbReader = cmd.ExecuteReader())
						{
							while (dbReader.Read())
							{
								UUID id = DBGuid.FromDB(dbReader["id"]);
								int typeInt = Convert.ToInt32(dbReader["assetType"], CultureInfo.InvariantCulture);
								sbyte type = Convert.ToSByte(typeInt, CultureInfo.InvariantCulture);
								pending.Add(new KeyValuePair<UUID, sbyte>(id, type));
							}
						}

						for (int i = 0; i < pending.Count; i++)
						{
							AssetMetadata metadata = GetMetadataByIdAndType(dbcon, pending[i].Key, pending[i].Value);
							if (metadata != null)
								result.Add(metadata);
						}
					}
				}
			}
			catch (Exception e)
			{
				m_log.ErrorFormat("[TSASSET DB]: MySQL failure fetching metadata set from {0}, count {1}. Error: {2}", start, count, e.Message);
			}

			return result;
		}

		public override bool Delete(string id)
		{
			if (!UUID.TryParse(id, out UUID assetId))
				return false;

			try
			{
				using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
				{
					dbcon.Open();

					using (MySqlTransaction tx = dbcon.BeginTransaction())
					{
						try
						{
							if (TryGetIndexedAssetType(dbcon, tx, assetId, out sbyte indexedType))
							{
								string typedTable = GetTypeTableName(indexedType);

								using (MySqlCommand deleteTyped = new MySqlCommand($"DELETE FROM `{typedTable}` WHERE id=?id", dbcon, tx))
								{
									deleteTyped.Parameters.AddWithValue("?id", assetId.ToString());
									deleteTyped.ExecuteNonQuery();
								}
							}

							using (MySqlCommand deleteIndex = new MySqlCommand($"DELETE FROM `{IndexTableName}` WHERE id=?id", dbcon, tx))
							{
								deleteIndex.Parameters.AddWithValue("?id", assetId.ToString());
								deleteIndex.ExecuteNonQuery();
							}

							if (m_fallbackToLegacy)
							{
								using (MySqlCommand deleteLegacy = new MySqlCommand($"DELETE FROM `{LegacyTableName}` WHERE id=?id", dbcon, tx))
								{
									deleteLegacy.Parameters.AddWithValue("?id", assetId.ToString());
									deleteLegacy.ExecuteNonQuery();
								}
							}

							tx.Commit();
							return true;
						}
						catch
						{
							tx.Rollback();
							throw;
						}
					}
				}
			}
			catch (Exception e)
			{
				m_log.ErrorFormat("[TSASSET DB]: MySQL failure deleting asset {0}. Error: {1}", id, e.Message);
				return false;
			}
		}

		public bool TryMoveAssets(string from, string to, out TSAssetMoveReport report, out string errorMessage)
		{
			TSAssetMoveOptions options = new TSAssetMoveOptions
			{
				ResetCheckpoint = false,
				BatchSize = 0,
				CommandTimeoutSeconds = 0
			};

			return TryMoveAssets(from, to, options, out report, out errorMessage);
		}

		public bool TryMoveAssets(string from, string to, TSAssetMoveOptions options, out TSAssetMoveReport report, out string errorMessage)
		{
			report = new TSAssetMoveReport
			{
				Source = from ?? string.Empty,
				Destination = to ?? string.Empty,
				CandidateCount = 0,
				AlreadyInTargetCount = 0,
				InsertedCount = 0,
				DeletedFromSourceCount = 0,
				IndexAffectedCount = 0
			};

			errorMessage = string.Empty;

			if (!TryParseMoveTable(from, out MoveTableSpec sourceSpec, out string parseSourceError))
			{
				errorMessage = parseSourceError;
				return false;
			}

			if (!TryParseMoveTable(to, out MoveTableSpec targetSpec, out string parseTargetError))
			{
				errorMessage = parseTargetError;
				return false;
			}

			if (sourceSpec.TableName.Equals(targetSpec.TableName, StringComparison.OrdinalIgnoreCase))
			{
				errorMessage = string.Format(CultureInfo.InvariantCulture, "Source and target resolve to the same table '{0}'", sourceSpec.TableName);
				return false;
			}

			try
			{
				using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
				{
					dbcon.Open();

					EnsureIndexTable(dbcon);

					if (!sourceSpec.IsLegacy && !TableExists(dbcon, sourceSpec.TableName))
						return true;

					if (targetSpec.IsLegacy)
					{
						if (!TableExists(dbcon, targetSpec.TableName))
						{
							errorMessage = string.Format(CultureInfo.InvariantCulture, "Target table '{0}' does not exist", targetSpec.TableName);
							return false;
						}
					}
					else
					{
						EnsureTypeTable(targetSpec.TableName);
					}

					string whereClause = BuildMoveWhereClause(sourceSpec, targetSpec);
					int sourceAssetTypeFilter = targetSpec.IsLegacy ? 0 : targetSpec.AssetType;
					int effectiveBatchSize = options.BatchSize > 0 ? options.BatchSize : m_tsAdminBatchSize;
					int effectiveCommandTimeoutSeconds = options.CommandTimeoutSeconds > 0 ? options.CommandTimeoutSeconds : m_tsAdminCommandTimeoutSeconds;
					string operationKey = BuildMoveOperationKey(sourceSpec, targetSpec);
					string cursorAfter = string.Empty;

					if (options.ResetCheckpoint)
						DeleteMoveCheckpoint(dbcon, operationKey, effectiveCommandTimeoutSeconds);

					cursorAfter = GetMoveCheckpoint(dbcon, operationKey, effectiveCommandTimeoutSeconds);

					while (true)
					{
						using (MySqlTransaction tx = dbcon.BeginTransaction())
						{
							try
							{
								List<string> batchIds = GetMoveBatchIds(
									dbcon,
									tx,
									sourceSpec,
									targetSpec,
									sourceAssetTypeFilter,
									whereClause,
									cursorAfter,
									effectiveBatchSize,
									effectiveCommandTimeoutSeconds);
								if (batchIds.Count == 0)
								{
									tx.Commit();
									break;
								}

								report.CandidateCount += batchIds.Count;

								int alreadyInTarget = CountRowsByIds(dbcon, tx, targetSpec.TableName, batchIds, effectiveCommandTimeoutSeconds);
								report.AlreadyInTargetCount += alreadyInTarget;

								InsertBatchIntoTarget(dbcon, tx, sourceSpec, targetSpec, batchIds, effectiveCommandTimeoutSeconds);

								int nowInTarget = CountRowsByIds(dbcon, tx, targetSpec.TableName, batchIds, effectiveCommandTimeoutSeconds);
								report.InsertedCount += Math.Max(0, nowInTarget - alreadyInTarget);

								if (targetSpec.IsLegacy)
									report.IndexAffectedCount += DeleteRowsByIds(dbcon, tx, IndexTableName, batchIds, effectiveCommandTimeoutSeconds);
								else
									report.IndexAffectedCount += UpsertIndexRowsByIds(dbcon, tx, targetSpec.AssetType, batchIds, effectiveCommandTimeoutSeconds);

								report.DeletedFromSourceCount += DeleteRowsByIds(dbcon, tx, sourceSpec.TableName, batchIds, effectiveCommandTimeoutSeconds);

								cursorAfter = batchIds[batchIds.Count - 1];
								UpsertMoveCheckpoint(dbcon, tx, operationKey, cursorAfter, effectiveCommandTimeoutSeconds);

								tx.Commit();
							}
							catch
							{
								TryRollback(tx);
								throw;
							}
						}
					}

					DeleteMoveCheckpoint(dbcon, operationKey, effectiveCommandTimeoutSeconds);

					return true;
				}
			}
			catch (Exception e)
			{
				errorMessage = BuildExceptionMessage(e);
				m_log.ErrorFormat("[TSASSET DB]: MySQL failure moving assets from {0} to {1}. Error: {2}", from, to, e.Message);
				return false;
			}
		}

		public bool TryPreviewMoveAssets(string from, string to, out TSAssetMoveReport report, out string errorMessage)
		{
			report = new TSAssetMoveReport
			{
				Source = from ?? string.Empty,
				Destination = to ?? string.Empty,
				CandidateCount = 0,
				AlreadyInTargetCount = 0,
				InsertedCount = 0,
				DeletedFromSourceCount = 0,
				IndexAffectedCount = 0
			};

			errorMessage = string.Empty;

			if (!TryParseMoveTable(from, out MoveTableSpec sourceSpec, out string parseSourceError))
			{
				errorMessage = parseSourceError;
				return false;
			}

			if (!TryParseMoveTable(to, out MoveTableSpec targetSpec, out string parseTargetError))
			{
				errorMessage = parseTargetError;
				return false;
			}

			if (sourceSpec.TableName.Equals(targetSpec.TableName, StringComparison.OrdinalIgnoreCase))
			{
				errorMessage = string.Format(CultureInfo.InvariantCulture, "Source and target resolve to the same table '{0}'", sourceSpec.TableName);
				return false;
			}

			try
			{
				using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
				{
					dbcon.Open();

					if (!sourceSpec.IsLegacy && !TableExists(dbcon, sourceSpec.TableName))
						return true;

					if (!targetSpec.IsLegacy && !TableExists(dbcon, targetSpec.TableName))
						EnsureTypeTable(targetSpec.TableName);

					if (targetSpec.IsLegacy && !TableExists(dbcon, targetSpec.TableName))
					{
						errorMessage = string.Format(CultureInfo.InvariantCulture, "Target table '{0}' does not exist", targetSpec.TableName);
						return false;
					}

					string whereClause = BuildMoveWhereClause(sourceSpec, targetSpec);
					int sourceAssetTypeFilter = targetSpec.IsLegacy ? 0 : targetSpec.AssetType;

					using (MySqlTransaction tx = dbcon.BeginTransaction(IsolationLevel.ReadCommitted))
					{
						try
						{
							report.CandidateCount = ExecuteCount(
								dbcon,
								tx,
								$"SELECT COUNT(*) FROM `{sourceSpec.TableName}` s {whereClause}",
								sourceAssetTypeFilter,
								sourceSpec,
								targetSpec);

							report.AlreadyInTargetCount = ExecuteCount(
								dbcon,
								tx,
								$"SELECT COUNT(*) FROM `{sourceSpec.TableName}` s INNER JOIN `{targetSpec.TableName}` t ON t.id = s.id {whereClause}",
								sourceAssetTypeFilter,
								sourceSpec,
								targetSpec);

							report.InsertedCount = Math.Max(0, report.CandidateCount - report.AlreadyInTargetCount);
							report.DeletedFromSourceCount = report.CandidateCount;

							if (targetSpec.IsLegacy)
								report.IndexAffectedCount = report.CandidateCount;
							else
								report.IndexAffectedCount = report.CandidateCount;

							tx.Commit();
							return true;
						}
						catch
						{
							TryRollback(tx);
							throw;
						}
					}
				}
			}
			catch (Exception e)
			{
				errorMessage = BuildExceptionMessage(e);
				m_log.ErrorFormat("[TSASSET DB]: MySQL failure previewing move from {0} to {1}. Error: {2}", from, to, e.Message);
				return false;
			}
		}

		public bool TryFindAssetLocation(string assetId, out TSAssetFindReport report, out string errorMessage)
		{
			report = new TSAssetFindReport
			{
				AssetId = assetId ?? string.Empty,
				Found = false,
				TableName = string.Empty,
				HasIndexEntry = false,
				IndexAssetType = 0
			};

			errorMessage = string.Empty;

			if (!UUID.TryParse(assetId, out UUID parsedId))
			{
				errorMessage = "Invalid asset UUID";
				return false;
			}

			try
			{
				using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
				{
					dbcon.Open();

					if (TryGetIndexedAssetType(dbcon, parsedId, out sbyte indexType))
					{
						report.HasIndexEntry = true;
						report.IndexAssetType = indexType;

						string typedTable = GetTypeTableName(indexType);
						if (TableExists(dbcon, typedTable))
						{
							if (ExecuteCountById(dbcon, typedTable, parsedId) > 0)
							{
								report.Found = true;
								report.TableName = typedTable;
								return true;
							}
						}
					}

					if (TableExists(dbcon, LegacyTableName) && ExecuteCountById(dbcon, LegacyTableName, parsedId) > 0)
					{
						report.Found = true;
						report.TableName = LegacyTableName;
						return true;
					}

					List<sbyte> types = GetExistingTypedTableTypes(dbcon);
					for (int i = 0; i < types.Count; i++)
					{
						string tableName = GetTypeTableName(types[i]);
						if (report.HasIndexEntry && tableName.Equals(GetTypeTableName((sbyte)report.IndexAssetType), StringComparison.OrdinalIgnoreCase))
							continue;

						if (ExecuteCountById(dbcon, tableName, parsedId) > 0)
						{
							report.Found = true;
							report.TableName = tableName;
							return true;
						}
					}

					return true;
				}
			}
			catch (Exception e)
			{
				errorMessage = e.Message;
				m_log.ErrorFormat("[TSASSET DB]: MySQL failure finding asset {0}. Error: {1}", assetId, e.Message);
				return false;
			}
		}

		public bool TryVerifyAssets(string scope, out TSAssetVerifyReport report, out string errorMessage)
		{
			report = new TSAssetVerifyReport
			{
				Scope = string.IsNullOrWhiteSpace(scope) ? "all" : scope.Trim(),
				TablesChecked = 0,
				TotalRows = 0,
				MissingIndexRows = 0,
				WrongIndexTypeRows = 0,
				OrphanIndexRows = 0,
				LegacyRowsWithIndex = 0
			};

			errorMessage = string.Empty;

			try
			{
				using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
				{
					dbcon.Open();
					EnsureIndexTable(dbcon);

					string normalizedScope = string.IsNullOrWhiteSpace(scope) ? "all" : scope.Trim();
					List<MoveTableSpec> verifyTables = new List<MoveTableSpec>();

					if (normalizedScope.Equals("all", StringComparison.OrdinalIgnoreCase))
					{
						if (TableExists(dbcon, LegacyTableName))
						{
							verifyTables.Add(new MoveTableSpec
							{
								TableName = LegacyTableName,
								IsLegacy = true,
								AssetType = 0
							});
						}

						List<sbyte> types = GetExistingTypedTableTypes(dbcon);
						for (int i = 0; i < types.Count; i++)
						{
							verifyTables.Add(new MoveTableSpec
							{
								TableName = GetTypeTableName(types[i]),
								IsLegacy = false,
								AssetType = types[i]
							});
						}
					}
					else
					{
						if (!TryParseMoveTable(normalizedScope, out MoveTableSpec requestedTable, out string parseError))
						{
							errorMessage = parseError;
							return false;
						}

						if (!TableExists(dbcon, requestedTable.TableName))
						{
							return true;
						}

						verifyTables.Add(requestedTable);
					}

					for (int i = 0; i < verifyTables.Count; i++)
					{
						MoveTableSpec table = verifyTables[i];
						report.TablesChecked++;
						report.TotalRows += ExecuteCountRaw(dbcon, string.Format(CultureInfo.InvariantCulture, "SELECT COUNT(*) FROM `{0}`", table.TableName));

						if (table.IsLegacy)
						{
							report.LegacyRowsWithIndex += ExecuteCountRaw(
								dbcon,
								$"SELECT COUNT(*) FROM `{LegacyTableName}` l INNER JOIN `{IndexTableName}` i ON i.id = l.id");
							continue;
						}

						report.MissingIndexRows += ExecuteCountRaw(
							dbcon,
							$"SELECT COUNT(*) FROM `{table.TableName}` t LEFT JOIN `{IndexTableName}` i ON i.id = t.id WHERE i.id IS NULL");

						report.WrongIndexTypeRows += ExecuteCountRaw(
							dbcon,
							$"SELECT COUNT(*) FROM `{table.TableName}` t INNER JOIN `{IndexTableName}` i ON i.id = t.id WHERE i.assetType <> {table.AssetType.ToString(CultureInfo.InvariantCulture)}");

						report.OrphanIndexRows += ExecuteCountRaw(
							dbcon,
							$"SELECT COUNT(*) FROM `{IndexTableName}` i LEFT JOIN `{table.TableName}` t ON t.id = i.id WHERE i.assetType = {table.AssetType.ToString(CultureInfo.InvariantCulture)} AND t.id IS NULL");
					}

					return true;
				}
			}
			catch (Exception e)
			{
				errorMessage = e.Message;
				m_log.ErrorFormat("[TSASSET DB]: MySQL failure verifying scope {0}. Error: {1}", scope, e.Message);
				return false;
			}
		}

		public bool TryReindexAssets(string scope, TSAssetMoveOptions options, out TSAssetReindexReport report, out string errorMessage)
		{
			report = new TSAssetReindexReport
			{
				Scope = string.IsNullOrWhiteSpace(scope) ? "all" : scope.Trim(),
				TablesProcessed = 0,
				RowsScanned = 0,
				IndexRowsUpserted = 0,
				IndexRowsDeleted = 0
			};

			errorMessage = string.Empty;

			try
			{
				using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
				{
					dbcon.Open();
					EnsureIndexTable(dbcon);
					EnsureMoveCheckpointTable(dbcon);

					int effectiveBatchSize = options.BatchSize > 0 ? options.BatchSize : m_tsAdminBatchSize;
					int effectiveCommandTimeoutSeconds = options.CommandTimeoutSeconds > 0 ? options.CommandTimeoutSeconds : m_tsAdminCommandTimeoutSeconds;

					if (!TryResolveScopeTables(dbcon, scope, out List<MoveTableSpec> tables, out errorMessage))
						return false;

					for (int tableIndex = 0; tableIndex < tables.Count; tableIndex++)
					{
						MoveTableSpec table = tables[tableIndex];
						report.TablesProcessed++;

						string opKey = string.Format(CultureInfo.InvariantCulture, "reindex:{0}", table.TableName);
						if (options.ResetCheckpoint)
							DeleteMoveCheckpoint(dbcon, opKey, effectiveCommandTimeoutSeconds);

						string cursorAfter = GetMoveCheckpoint(dbcon, opKey, effectiveCommandTimeoutSeconds);

						while (true)
						{
							using (MySqlTransaction tx = dbcon.BeginTransaction())
							{
								try
								{
									List<string> ids = GetBatchIdsByTable(dbcon, tx, table.TableName, cursorAfter, effectiveBatchSize, effectiveCommandTimeoutSeconds);
									if (ids.Count == 0)
									{
										tx.Commit();
										break;
									}

									report.RowsScanned += ids.Count;

									if (table.IsLegacy)
										report.IndexRowsDeleted += DeleteRowsByIds(dbcon, tx, IndexTableName, ids, effectiveCommandTimeoutSeconds);
									else
										report.IndexRowsUpserted += UpsertIndexRowsByIds(dbcon, tx, table.AssetType, ids, effectiveCommandTimeoutSeconds);

									cursorAfter = ids[ids.Count - 1];
									UpsertMoveCheckpoint(dbcon, tx, opKey, cursorAfter, effectiveCommandTimeoutSeconds);

									tx.Commit();
								}
								catch
								{
									TryRollback(tx);
									throw;
								}
							}
						}

						DeleteMoveCheckpoint(dbcon, opKey, effectiveCommandTimeoutSeconds);
					}

					return true;
				}
			}
			catch (Exception e)
			{
				errorMessage = BuildExceptionMessage(e);
				m_log.ErrorFormat("[TSASSET DB]: MySQL failure reindexing scope {0}. Error: {1}", scope, e.Message);
				return false;
			}
		}

		public bool TryCleanLegacyIndex(TSAssetMoveOptions options, out TSAssetReindexReport report, out string errorMessage)
		{
			return TryReindexAssets("assets", options, out report, out errorMessage);
		}

		#endregion

		private sealed class MoveTableSpec
		{
			public string TableName;
			public bool IsLegacy;
			public sbyte AssetType;
		}

		private static bool TryParseMoveTable(string token, out MoveTableSpec spec, out string error)
		{
			spec = null;
			error = string.Empty;

			if (string.IsNullOrWhiteSpace(token))
			{
				error = "Table token is empty";
				return false;
			}

			string normalized = token.Trim();

			if (normalized.Equals(LegacyTableName, StringComparison.OrdinalIgnoreCase))
			{
				spec = new MoveTableSpec
				{
					TableName = LegacyTableName,
					IsLegacy = true,
					AssetType = 0
				};
				return true;
			}

			if (sbyte.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out sbyte directType))
			{
				spec = new MoveTableSpec
				{
					TableName = GetTypeTableName(directType),
					IsLegacy = false,
					AssetType = directType
				};
				return true;
			}

			Match typedName = Regex.Match(normalized, "^assets_(-?\\d+)$", RegexOptions.IgnoreCase);
			if (typedName.Success)
			{
				if (sbyte.TryParse(typedName.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out sbyte parsedType))
				{
					spec = new MoveTableSpec
					{
						TableName = GetTypeTableName(parsedType),
						IsLegacy = false,
						AssetType = parsedType
					};
					return true;
				}
			}

			error = string.Format(CultureInfo.InvariantCulture, "Invalid table/type token '{0}'. Use 'assets', '<type>' or 'assets_<type>'", token);
			return false;
		}

		private bool TryResolveScopeTables(MySqlConnection dbcon, string scope, out List<MoveTableSpec> tables, out string error)
		{
			tables = new List<MoveTableSpec>();
			error = string.Empty;

			string normalizedScope = string.IsNullOrWhiteSpace(scope) ? "all" : scope.Trim();

			if (normalizedScope.Equals("all", StringComparison.OrdinalIgnoreCase))
			{
				if (TableExists(dbcon, LegacyTableName))
				{
					tables.Add(new MoveTableSpec
					{
						TableName = LegacyTableName,
						IsLegacy = true,
						AssetType = 0
					});
				}

				List<sbyte> types = GetExistingTypedTableTypes(dbcon);
				for (int i = 0; i < types.Count; i++)
				{
					tables.Add(new MoveTableSpec
					{
						TableName = GetTypeTableName(types[i]),
						IsLegacy = false,
						AssetType = types[i]
					});
				}

				return true;
			}

			if (!TryParseMoveTable(normalizedScope, out MoveTableSpec requestedTable, out string parseError))
			{
				error = parseError;
				return false;
			}

			if (!TableExists(dbcon, requestedTable.TableName))
				return true;

			tables.Add(requestedTable);
			return true;
		}

		private static string BuildMoveWhereClause(MoveTableSpec source, MoveTableSpec target)
		{
			if (source.IsLegacy && !target.IsLegacy)
				return "WHERE s.assetType = ?sourceTypeFilter";

			return string.Empty;
		}

		private static void BindMoveParameters(MySqlCommand cmd, int sourceAssetTypeFilter, MoveTableSpec source, MoveTableSpec target)
		{
			if (source.IsLegacy && !target.IsLegacy)
				cmd.Parameters.AddWithValue("?sourceTypeFilter", sourceAssetTypeFilter);
		}

		private static bool TableExists(MySqlConnection dbcon, string tableName)
		{
			using (MySqlCommand cmd = new MySqlCommand(
				"SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = ?tableName LIMIT 1",
				dbcon))
			{
				cmd.Parameters.AddWithValue("?tableName", tableName);
				object value = cmd.ExecuteScalar();
				return value != null && value != DBNull.Value;
			}
		}

		private static int ExecuteCountById(MySqlConnection dbcon, string tableName, UUID id)
		{
			using (MySqlCommand cmd = new MySqlCommand($"SELECT COUNT(*) FROM `{tableName}` WHERE id=?id", dbcon))
			{
				cmd.Parameters.AddWithValue("?id", id.ToString());
				object scalar = cmd.ExecuteScalar();
				if (scalar == null || scalar is DBNull)
					return 0;

				return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
			}
		}

		private static int ExecuteCountRaw(MySqlConnection dbcon, string sql)
		{
			using (MySqlCommand cmd = new MySqlCommand(sql, dbcon))
			{
				object scalar = cmd.ExecuteScalar();
				if (scalar == null || scalar is DBNull)
					return 0;

				return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
			}
		}

		private static List<sbyte> GetExistingTypedTableTypes(MySqlConnection dbcon)
		{
			List<sbyte> result = new List<sbyte>();

			using (MySqlCommand cmd = new MySqlCommand(
				"SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name REGEXP '^assets_-?[0-9]+$'",
				dbcon))
			using (MySqlDataReader reader = cmd.ExecuteReader())
			{
				while (reader.Read())
				{
					string tableName = reader["table_name"].ToString();
					Match typedMatch = Regex.Match(tableName, "^assets_(-?\\d+)$", RegexOptions.IgnoreCase);
					if (!typedMatch.Success)
						continue;

					if (sbyte.TryParse(typedMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out sbyte type))
						result.Add(type);
				}
			}

			result.Sort();
			return result;
		}

		private static int ExecuteCount(MySqlConnection dbcon, MySqlTransaction tx, string sql, int sourceAssetTypeFilter, MoveTableSpec source, MoveTableSpec target)
		{
			using (MySqlCommand cmd = new MySqlCommand(sql, dbcon, tx))
			{
				cmd.CommandTimeout = DefaultTsAdminCommandTimeoutSeconds;
				BindMoveParameters(cmd, sourceAssetTypeFilter, source, target);
				object scalar = cmd.ExecuteScalar();
				if (scalar == null || scalar is DBNull)
					return 0;

				return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
			}
		}

		private static List<string> GetMoveBatchIds(
			MySqlConnection dbcon,
			MySqlTransaction tx,
			MoveTableSpec sourceSpec,
			MoveTableSpec targetSpec,
			int sourceAssetTypeFilter,
			string whereClause,
			string cursorAfter,
			int batchSize,
			int commandTimeoutSeconds)
		{
			List<string> ids = new List<string>(batchSize);
			string scopedWhere = BuildScopedWhereClause(whereClause, !string.IsNullOrEmpty(cursorAfter));

			using (MySqlCommand cmd = new MySqlCommand(
				$"SELECT s.id FROM `{sourceSpec.TableName}` s {scopedWhere} ORDER BY s.id LIMIT ?limit",
				dbcon,
				tx))
			{
				cmd.CommandTimeout = commandTimeoutSeconds;
				BindMoveParameters(cmd, sourceAssetTypeFilter, sourceSpec, targetSpec);

				if (!string.IsNullOrEmpty(cursorAfter))
					cmd.Parameters.AddWithValue("?cursorAfter", cursorAfter);

				cmd.Parameters.AddWithValue("?limit", batchSize);

				using (MySqlDataReader reader = cmd.ExecuteReader())
				{
					while (reader.Read())
						ids.Add(reader["id"].ToString());
				}
			}

			return ids;
		}

		private static List<string> GetBatchIdsByTable(
			MySqlConnection dbcon,
			MySqlTransaction tx,
			string tableName,
			string cursorAfter,
			int batchSize,
			int commandTimeoutSeconds)
		{
			List<string> ids = new List<string>(batchSize);
			string whereClause = string.IsNullOrEmpty(cursorAfter) ? string.Empty : "WHERE id > ?cursorAfter";

			using (MySqlCommand cmd = new MySqlCommand(
				$"SELECT id FROM `{tableName}` {whereClause} ORDER BY id LIMIT ?limit",
				dbcon,
				tx))
			{
				cmd.CommandTimeout = commandTimeoutSeconds;
				if (!string.IsNullOrEmpty(cursorAfter))
					cmd.Parameters.AddWithValue("?cursorAfter", cursorAfter);

				cmd.Parameters.AddWithValue("?limit", batchSize);

				using (MySqlDataReader reader = cmd.ExecuteReader())
				{
					while (reader.Read())
						ids.Add(reader["id"].ToString());
				}
			}

			return ids;
		}

		private static int CountRowsByIds(MySqlConnection dbcon, MySqlTransaction tx, string tableName, List<string> ids, int commandTimeoutSeconds)
		{
			if (ids == null || ids.Count == 0)
				return 0;

			using (MySqlCommand cmd = new MySqlCommand(
				$"SELECT COUNT(*) FROM `{tableName}` WHERE id IN ({BuildIdInClause(ids.Count)})",
				dbcon,
				tx))
			{
				cmd.CommandTimeout = commandTimeoutSeconds;
				AddIdParameters(cmd, ids);
				object scalar = cmd.ExecuteScalar();
				if (scalar == null || scalar is DBNull)
					return 0;

				return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
			}
		}

		private static void InsertBatchIntoTarget(MySqlConnection dbcon, MySqlTransaction tx, MoveTableSpec sourceSpec, MoveTableSpec targetSpec, List<string> ids, int commandTimeoutSeconds)
		{
			if (ids == null || ids.Count == 0)
				return;

			using (MySqlCommand cmd = new MySqlCommand(
				$"INSERT IGNORE INTO `{targetSpec.TableName}` " +
				"(id, name, description, assetType, local, temporary, create_time, access_time, asset_flags, CreatorID, data) " +
				"SELECT s.id, s.name, s.description, " +
				(targetSpec.IsLegacy ? "s.assetType" : "?targetAssetType") +
				", s.local, s.temporary, s.create_time, s.access_time, s.asset_flags, s.CreatorID, s.data " +
				$"FROM `{sourceSpec.TableName}` s WHERE s.id IN ({BuildIdInClause(ids.Count)})",
				dbcon,
				tx))
			{
				cmd.CommandTimeout = commandTimeoutSeconds;
				AddIdParameters(cmd, ids);

				if (!targetSpec.IsLegacy)
					cmd.Parameters.AddWithValue("?targetAssetType", targetSpec.AssetType);

				cmd.ExecuteNonQuery();
			}
		}

		private static int DeleteRowsByIds(MySqlConnection dbcon, MySqlTransaction tx, string tableName, List<string> ids, int commandTimeoutSeconds)
		{
			if (ids == null || ids.Count == 0)
				return 0;

			using (MySqlCommand cmd = new MySqlCommand(
				$"DELETE FROM `{tableName}` WHERE id IN ({BuildIdInClause(ids.Count)})",
				dbcon,
				tx))
			{
				cmd.CommandTimeout = commandTimeoutSeconds;
				AddIdParameters(cmd, ids);
				return cmd.ExecuteNonQuery();
			}
		}

		private static int UpsertIndexRowsByIds(MySqlConnection dbcon, MySqlTransaction tx, sbyte targetType, List<string> ids, int commandTimeoutSeconds)
		{
			if (ids == null || ids.Count == 0)
				return 0;

			using (MySqlCommand cmd = new MySqlCommand(
				$"INSERT INTO `{IndexTableName}` (id, assetType, updated_at) " +
				$"SELECT id, ?assetType, ?updatedAt FROM `{GetTypeTableName(targetType)}` WHERE id IN ({BuildIdInClause(ids.Count)}) " +
				"ON DUPLICATE KEY UPDATE assetType = VALUES(assetType), updated_at = VALUES(updated_at)",
				dbcon,
				tx))
			{
				cmd.CommandTimeout = commandTimeoutSeconds;
				cmd.Parameters.AddWithValue("?assetType", targetType);
				cmd.Parameters.AddWithValue("?updatedAt", (int)Utils.DateTimeToUnixTime(DateTime.UtcNow));
				AddIdParameters(cmd, ids);
				return cmd.ExecuteNonQuery();
			}
		}

		private static string BuildScopedWhereClause(string whereClause, bool includeCursor)
		{
			if (!includeCursor)
				return whereClause;

			if (string.IsNullOrWhiteSpace(whereClause))
				return "WHERE s.id > ?cursorAfter";

			return string.Concat(whereClause, " AND s.id > ?cursorAfter");
		}

		private static string BuildMoveOperationKey(MoveTableSpec sourceSpec, MoveTableSpec targetSpec)
		{
			return string.Format(CultureInfo.InvariantCulture, "{0}->{1}", sourceSpec.TableName, targetSpec.TableName);
		}

		private static void EnsureMoveCheckpointTable(MySqlConnection dbcon)
		{
			string sql =
				$"CREATE TABLE IF NOT EXISTS `{MoveCheckpointTableName}` (" +
				"`op_key` varchar(190) NOT NULL," +
				"`last_id` char(36) NOT NULL DEFAULT ''," +
				"`updated_at` int(11) NOT NULL DEFAULT '0'," +
				"PRIMARY KEY (`op_key`)" +
				") ENGINE=InnoDB DEFAULT CHARSET=utf8";

			using (MySqlCommand cmd = new MySqlCommand(sql, dbcon))
				cmd.ExecuteNonQuery();
		}

		private static string GetMoveCheckpoint(MySqlConnection dbcon, string operationKey, int commandTimeoutSeconds)
		{
			using (MySqlCommand cmd = new MySqlCommand(
				$"SELECT last_id FROM `{MoveCheckpointTableName}` WHERE op_key=?opKey",
				dbcon))
			{
				cmd.CommandTimeout = commandTimeoutSeconds;
				cmd.Parameters.AddWithValue("?opKey", operationKey);
				object scalar = cmd.ExecuteScalar();
				if (scalar == null || scalar is DBNull)
					return string.Empty;

				return scalar.ToString();
			}
		}

		private static void UpsertMoveCheckpoint(MySqlConnection dbcon, MySqlTransaction tx, string operationKey, string lastId, int commandTimeoutSeconds)
		{
			using (MySqlCommand cmd = new MySqlCommand(
				$"INSERT INTO `{MoveCheckpointTableName}` (op_key, last_id, updated_at) VALUES (?opKey, ?lastId, ?updatedAt) " +
				"ON DUPLICATE KEY UPDATE last_id = VALUES(last_id), updated_at = VALUES(updated_at)",
				dbcon,
				tx))
			{
				cmd.CommandTimeout = commandTimeoutSeconds;
				cmd.Parameters.AddWithValue("?opKey", operationKey);
				cmd.Parameters.AddWithValue("?lastId", lastId ?? string.Empty);
				cmd.Parameters.AddWithValue("?updatedAt", (int)Utils.DateTimeToUnixTime(DateTime.UtcNow));
				cmd.ExecuteNonQuery();
			}
		}

		private static void DeleteMoveCheckpoint(MySqlConnection dbcon, string operationKey, int commandTimeoutSeconds)
		{
			using (MySqlCommand cmd = new MySqlCommand(
				$"DELETE FROM `{MoveCheckpointTableName}` WHERE op_key=?opKey",
				dbcon))
			{
				cmd.CommandTimeout = commandTimeoutSeconds;
				cmd.Parameters.AddWithValue("?opKey", operationKey);
				cmd.ExecuteNonQuery();
			}
		}

		private static string BuildIdInClause(int count)
		{
			string[] placeholders = new string[count];
			for (int i = 0; i < count; i++)
				placeholders[i] = "?id" + i.ToString(CultureInfo.InvariantCulture);

			return string.Join(",", placeholders);
		}

		private static void AddIdParameters(MySqlCommand cmd, List<string> ids)
		{
			for (int i = 0; i < ids.Count; i++)
				cmd.Parameters.AddWithValue("?id" + i.ToString(CultureInfo.InvariantCulture), ids[i]);
		}

		private static void TryRollback(MySqlTransaction tx)
		{
			if (tx == null)
				return;

			try
			{
				tx.Rollback();
			}
			catch
			{
			}
		}

		private static string BuildExceptionMessage(Exception e)
		{
			if (e == null)
				return string.Empty;

			string message = e.Message;
			Exception inner = e.InnerException;

			while (inner != null)
			{
				if (!string.IsNullOrEmpty(inner.Message))
					message = string.Concat(message, " | ", inner.Message);

				inner = inner.InnerException;
			}

			return message;
		}

		private static int TryReadIntSetting(string connectionString, string key, int defaultValue)
		{
			if (string.IsNullOrEmpty(connectionString) || string.IsNullOrEmpty(key))
				return defaultValue;

			string[] parts = connectionString.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
			foreach (string rawPart in parts)
			{
				string part = rawPart.Trim();
				int eqIndex = part.IndexOf('=');
				if (eqIndex <= 0 || eqIndex == part.Length - 1)
					continue;

				string k = part.Substring(0, eqIndex).Trim();
				if (!k.Equals(key, StringComparison.OrdinalIgnoreCase))
					continue;

				string v = part.Substring(eqIndex + 1).Trim();
				if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
					return parsed;
			}

			return defaultValue;
		}

		private static bool TryReadBooleanSetting(string connectionString, string key, bool defaultValue)
		{
			if (string.IsNullOrEmpty(connectionString) || string.IsNullOrEmpty(key))
				return defaultValue;

			string[] parts = connectionString.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
			foreach (string rawPart in parts)
			{
				string part = rawPart.Trim();
				int eqIndex = part.IndexOf('=');
				if (eqIndex <= 0 || eqIndex == part.Length - 1)
					continue;

				string k = part.Substring(0, eqIndex).Trim();
				if (!k.Equals(key, StringComparison.OrdinalIgnoreCase))
					continue;

				string v = part.Substring(eqIndex + 1).Trim();
				if (v.Equals("1", StringComparison.OrdinalIgnoreCase) || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase))
					return true;

				if (v.Equals("0", StringComparison.OrdinalIgnoreCase) || v.Equals("false", StringComparison.OrdinalIgnoreCase) || v.Equals("no", StringComparison.OrdinalIgnoreCase))
					return false;
			}

			return defaultValue;
		}

		private static string GetTypeTableName(sbyte assetType)
		{
			return string.Format(CultureInfo.InvariantCulture, TypedTableNameFormat, assetType);
		}

		private void EnsureTypeTable(string tableName)
		{
			lock (m_tableSync)
			{
				if (m_initializedTables.Contains(tableName))
					return;

				using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
				{
					dbcon.Open();
					CreateTypeTable(dbcon, tableName);
				}

				m_initializedTables.Add(tableName);
			}
		}

		private void CreateTypeTable(MySqlConnection dbcon, string tableName)
		{
			string sql =
				$"CREATE TABLE IF NOT EXISTS `{tableName}` (" +
				"`name` varchar(64) NOT NULL," +
				"`description` varchar(64) NOT NULL," +
				"`assetType` tinyint(4) NOT NULL," +
				"`local` tinyint(1) NOT NULL," +
				"`temporary` tinyint(1) NOT NULL," +
				"`data` longblob NOT NULL," +
				"`id` char(36) NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'," +
				"`create_time` int(11) DEFAULT '0'," +
				"`access_time` int(11) DEFAULT '0'," +
				"`asset_flags` int(11) NOT NULL DEFAULT '0'," +
				"`CreatorID` varchar(128) NOT NULL DEFAULT ''," +
				"PRIMARY KEY (`id`)" +
				") ENGINE=InnoDB DEFAULT CHARSET=utf8";

			using (MySqlCommand cmd = new MySqlCommand(sql, dbcon))
			{
				cmd.ExecuteNonQuery();
			}
		}

		private static void EnsureIndexTable(MySqlConnection dbcon)
		{
			string sql =
				$"CREATE TABLE IF NOT EXISTS `{IndexTableName}` (" +
				"`id` char(36) NOT NULL," +
				"`assetType` tinyint(4) NOT NULL," +
				"`updated_at` int(11) NOT NULL DEFAULT '0'," +
				"PRIMARY KEY (`id`)," +
				$"INDEX `idx_{IndexTableName}_assetType` (`assetType`)" +
				") ENGINE=InnoDB DEFAULT CHARSET=utf8";

			using (MySqlCommand cmd = new MySqlCommand(sql, dbcon))
			{
				cmd.ExecuteNonQuery();
			}
		}

		private bool TryStoreTypedWithExistingConnection(MySqlConnection dbcon, AssetBase asset)
		{
			string tableName = GetTypeTableName(asset.Type);
			EnsureTypeTable(tableName);

			string assetName = asset.Name ?? string.Empty;
			if (assetName.Length > AssetBase.MAX_ASSET_NAME)
				assetName = assetName.Substring(0, AssetBase.MAX_ASSET_NAME);

			string assetDescription = asset.Description ?? string.Empty;
			if (assetDescription.Length > AssetBase.MAX_ASSET_DESC)
				assetDescription = assetDescription.Substring(0, AssetBase.MAX_ASSET_DESC);

			int now = (int)Utils.DateTimeToUnixTime(DateTime.UtcNow);

			using (MySqlTransaction tx = dbcon.BeginTransaction())
			{
				try
				{
					string upsertAssetSql =
						$"REPLACE INTO `{tableName}` " +
						"(id, name, description, assetType, local, temporary, create_time, access_time, asset_flags, CreatorID, data) " +
						"VALUES(?id, ?name, ?description, ?assetType, ?local, ?temporary, ?create_time, ?access_time, ?asset_flags, ?CreatorID, ?data)";

					using (MySqlCommand assetCmd = new MySqlCommand(upsertAssetSql, dbcon, tx))
					{
						assetCmd.Parameters.AddWithValue("?id", asset.ID);
						assetCmd.Parameters.AddWithValue("?name", assetName);
						assetCmd.Parameters.AddWithValue("?description", assetDescription);
						assetCmd.Parameters.AddWithValue("?assetType", asset.Type);
						assetCmd.Parameters.AddWithValue("?local", asset.Local);
						assetCmd.Parameters.AddWithValue("?temporary", asset.Temporary);
						assetCmd.Parameters.AddWithValue("?create_time", now);
						assetCmd.Parameters.AddWithValue("?access_time", now);
						assetCmd.Parameters.AddWithValue("?asset_flags", (int)asset.Flags);
						assetCmd.Parameters.AddWithValue("?CreatorID", asset.Metadata.CreatorID ?? string.Empty);
						assetCmd.Parameters.AddWithValue("?data", asset.Data ?? Array.Empty<byte>());
						assetCmd.ExecuteNonQuery();
					}

					using (MySqlCommand indexCmd = new MySqlCommand(
						$"REPLACE INTO `{IndexTableName}` (id, assetType, updated_at) VALUES (?id, ?assetType, ?updated_at)",
						dbcon,
						tx))
					{
						indexCmd.Parameters.AddWithValue("?id", asset.ID);
						indexCmd.Parameters.AddWithValue("?assetType", asset.Type);
						indexCmd.Parameters.AddWithValue("?updated_at", now);
						indexCmd.ExecuteNonQuery();
					}

					tx.Commit();
					return true;
				}
				catch
				{
					tx.Rollback();
					throw;
				}
			}
		}

		private AssetBase GetAssetFromTable(MySqlConnection dbcon, string tableName, UUID assetID)
		{
			string sql =
				$"SELECT name, description, assetType, local, temporary, asset_flags, CreatorID, data " +
				$"FROM `{tableName}` WHERE id=?id";

			try
			{
				using (MySqlCommand cmd = new MySqlCommand(sql, dbcon))
				{
					cmd.Parameters.AddWithValue("?id", assetID.ToString());

					using (MySqlDataReader dbReader = cmd.ExecuteReader(CommandBehavior.SingleRow))
					{
						if (!dbReader.Read())
							return null;

						AssetBase asset = new AssetBase(assetID, (string)dbReader["name"], (sbyte)dbReader["assetType"], dbReader["CreatorID"].ToString());
						asset.Description = (string)dbReader["description"];
						asset.Local = Convert.ToBoolean(dbReader["local"]);
						asset.Temporary = Convert.ToBoolean(dbReader["temporary"]);
						asset.Flags = (AssetFlags)Convert.ToInt32(dbReader["asset_flags"], CultureInfo.InvariantCulture);
						asset.Data = (byte[])dbReader["data"];
						return asset;
					}
				}
			}
			catch (MySqlException)
			{
				return null;
			}
		}

		private AssetMetadata GetMetadataByIdAndType(MySqlConnection dbcon, UUID id, sbyte type)
		{
			string tableName = GetTypeTableName(type);

			string sql =
				$"SELECT id, name, description, assetType, temporary, asset_flags, CreatorID " +
				$"FROM `{tableName}` WHERE id=?id";

			try
			{
				using (MySqlCommand cmd = new MySqlCommand(sql, dbcon))
				{
					cmd.Parameters.AddWithValue("?id", id.ToString());
					using (MySqlDataReader dbReader = cmd.ExecuteReader(CommandBehavior.SingleRow))
					{
						if (dbReader.Read())
							return ReadMetadata(dbReader);
					}
				}
			}
			catch (MySqlException)
			{
				// Type table may not yet exist if no asset of this type was stored.
			}

			if (!m_fallbackToLegacy)
				return null;

			using (MySqlCommand legacyCmd = new MySqlCommand(
				$"SELECT id, name, description, assetType, temporary, asset_flags, CreatorID FROM `{LegacyTableName}` WHERE id=?id",
				dbcon))
			{
				legacyCmd.Parameters.AddWithValue("?id", id.ToString());
				using (MySqlDataReader dbReader = legacyCmd.ExecuteReader(CommandBehavior.SingleRow))
				{
					if (dbReader.Read())
						return ReadMetadata(dbReader);
				}
			}

			return null;
		}

		private static AssetMetadata ReadMetadata(MySqlDataReader dbReader)
		{
			AssetMetadata metadata = new AssetMetadata();
			metadata.FullID = DBGuid.FromDB(dbReader["id"]);
			metadata.Name = dbReader["name"].ToString();
			metadata.Description = dbReader["description"].ToString();
			metadata.Type = Convert.ToSByte(dbReader["assetType"], CultureInfo.InvariantCulture);
			metadata.Temporary = Convert.ToBoolean(dbReader["temporary"]);
			metadata.Flags = (AssetFlags)Convert.ToInt32(dbReader["asset_flags"], CultureInfo.InvariantCulture);
			metadata.CreatorID = dbReader["CreatorID"].ToString();
			metadata.SHA1 = Array.Empty<byte>();
			return metadata;
		}

		private static UUID[] BuildMissingArray(UUID[] requested, HashSet<UUID> found)
		{
			List<UUID> missing = new List<UUID>(requested.Length);
			for (int i = 0; i < requested.Length; i++)
			{
				if (!found.Contains(requested[i]))
					missing.Add(requested[i]);
			}

			return missing.ToArray();
		}

		private static HashSet<UUID> QueryExistingIds(MySqlConnection dbcon, string tableName, string idColumn, UUID[] uuids)
		{
			HashSet<UUID> found = new HashSet<UUID>();

			if (uuids.Length == 0)
				return found;

			const int batchSize = 200;
			for (int offset = 0; offset < uuids.Length; offset += batchSize)
			{
				int take = Math.Min(batchSize, uuids.Length - offset);
				string[] placeholders = new string[take];

				using (MySqlCommand cmd = dbcon.CreateCommand())
				{
					for (int i = 0; i < take; i++)
					{
						string paramName = "?id" + i.ToString(CultureInfo.InvariantCulture);
						placeholders[i] = paramName;
						cmd.Parameters.AddWithValue(paramName, uuids[offset + i].ToString());
					}

					cmd.CommandText = string.Format(
						CultureInfo.InvariantCulture,
						"SELECT {0} FROM `{1}` WHERE {0} IN ({2})",
						idColumn,
						tableName,
						string.Join(",", placeholders));

					using (MySqlDataReader dbReader = cmd.ExecuteReader())
					{
						while (dbReader.Read())
						{
							UUID id = DBGuid.FromDB(dbReader[idColumn]);
							found.Add(id);
						}
					}
				}
			}

			return found;
		}

		private bool TryGetIndexedAssetType(MySqlConnection dbcon, UUID assetID, out sbyte assetType)
		{
			return TryGetIndexedAssetType(dbcon, null, assetID, out assetType);
		}

		private bool TryGetIndexedAssetType(MySqlConnection dbcon, MySqlTransaction tx, UUID assetID, out sbyte assetType)
		{
			using (MySqlCommand cmd = new MySqlCommand($"SELECT assetType FROM `{IndexTableName}` WHERE id=?id", dbcon, tx))
			{
				cmd.Parameters.AddWithValue("?id", assetID.ToString());

				object value = cmd.ExecuteScalar();
				if (value == null || value is DBNull)
				{
					assetType = 0;
					return false;
				}

				int typeInt = Convert.ToInt32(value, CultureInfo.InvariantCulture);
				if (typeInt < sbyte.MinValue || typeInt > sbyte.MaxValue)
				{
					assetType = 0;
					return false;
				}

				assetType = (sbyte)typeInt;
				return true;
			}
		}

		private static void RemoveIndexRow(MySqlConnection dbcon, UUID assetID)
		{
			using (MySqlCommand cmd = new MySqlCommand($"DELETE FROM `{IndexTableName}` WHERE id=?id", dbcon))
			{
				cmd.Parameters.AddWithValue("?id", assetID.ToString());
				cmd.ExecuteNonQuery();
			}
		}
	}
}
