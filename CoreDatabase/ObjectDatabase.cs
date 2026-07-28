using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using DOL.Database.Attributes;
using DOL.Database.Connection;
using DOL.Database.Handlers;
using DOL.Logging;

namespace DOL.Database
{
	/// <summary>
	/// Default Object Database Base Implementation
	/// </summary>
	public abstract class ObjectDatabase : IObjectDatabase
	{
		protected static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

		protected const long LONG_EXEC_THRESHOLD = 100;

		/// <summary>
		/// Number Format Info to Use for Database
		/// </summary>
		protected static readonly NumberFormatInfo Nfi = new CultureInfo("en-US", false).NumberFormat;

		private static readonly ConcurrentDictionary<Type, Func<IEnumerable<DataObject>, Array>> _castToArrayCache = new();
		private readonly ConcurrentDictionary<ElementBinding, RelationDescriptor> _relationDescriptorCache = new();

		/// <summary>
		/// Data Table Handlers for this Database Handler
		/// </summary>
		protected readonly Dictionary<string, DataTableHandler> TableDatasets = new Dictionary<string, DataTableHandler>();

		/// <summary>
		/// Connection String for this Database
		/// </summary>
		protected string ConnectionString { get; set; }
		
		/// <summary>
		/// Creates a new Instance of <see cref="ObjectDatabase"/>
		/// </summary>
		/// <param name="ConnectionString">Database Connection String</param>
		protected ObjectDatabase(string ConnectionString)
		{
			this.ConnectionString = ConnectionString;
		}
		
		/// <summary>
		/// Helper to Retrieve Table Handler from Object Type
		/// Return Real Table Handler for Modifications Queries
		/// </summary>
		/// <param name="objectType">Object Type</param>
		/// <returns>DataTableHandler for this Object Type or null.</returns>
		protected DataTableHandler GetTableHandler(Type objectType)
		{
			var tableName = AttributeUtil.GetTableName(objectType);
			DataTableHandler handler;
			return TableDatasets.TryGetValue(tableName, out handler) ? handler : null;
		}
		
		/// <summary>
		/// Helper to Retrieve Table or View Handler from Object Type
		/// Return View or Table for Select Queries
		/// </summary>
		/// <param name="objectType">Object Type</param>
		/// <returns>DataTableHandler for this Object Type or null.</returns>
		protected DataTableHandler GetTableOrViewHandler(Type objectType)
		{
			var tableName = AttributeUtil.GetTableOrViewName(objectType);
			DataTableHandler handler;
			return TableDatasets.TryGetValue(tableName, out handler) ? handler : null;
		}

		#region Public Add Objects Implementation
		/// <summary>
		/// Insert a new DataObject into the database and save it
		/// </summary>
		/// <param name="dataObject">DataObject to Add into database</param>
		/// <returns>True if the DataObject was added.</returns>
		public bool AddObject(DataObject dataObject)
		{
			return AddObject(new [] { dataObject });
		}
		
		/// <summary>
		/// Insert new DataObjects into the database and save them
		/// </summary>
		/// <param name="dataObjects">DataObjects to Add into database</param>
		/// <returns>True if All DataObjects were added.</returns>
		public bool AddObject(IEnumerable<DataObject> dataObjects)
		{
			var success = true;
			foreach (var grp in dataObjects.GroupBy(obj => obj.GetType()))
			{
				var tableHandler = GetTableHandler(grp.Key);
				
				if (tableHandler == null)
				{
					if (log.IsErrorEnabled)
						log.ErrorFormat("AddObject: DataObject Type ({0}) not registered !", grp.Key.FullName);
					success = false;
					continue;
				}
				
				foreach (var allowed in grp.GroupBy(item => item.AllowAdd))
				{
					if (allowed.Key)
					{
						var objs = allowed.ToArray();
						var results = AddObjectImpl(tableHandler, objs);
						
						var resultsByObjs = results.Select((result, index) => new { Success = result, DataObject = objs[index] })
							.GroupBy(obj => obj.Success);
						
						foreach (var resultGrp in resultsByObjs)
						{
							if (resultGrp.Key)
							{
								// Save in Precache if tablehandler use it
								if (tableHandler.UsesPreCaching)
								{
									var primary = tableHandler.PrimaryKey;
									if (primary != null)
									{
										foreach (var successObj in resultGrp.Select(obj => obj.DataObject))
											tableHandler.SetPreCachedObject(primary.GetValue(successObj), successObj);
									}
								}
								
								// Success Objects Need Relations Save
								if (tableHandler.HasRelations)
									success &= SaveObjectRelations(tableHandler, resultGrp.Select(obj => obj.DataObject));
							}
							else
							{
								if (log.IsErrorEnabled)
								{
									foreach(var obj in resultGrp)
										log.ErrorFormat("AddObjects: DataObject ({0}) could not be inserted into database...", obj.DataObject);
								}
								success = false;
							}
						}
					}
					else
					{
						if (log.IsWarnEnabled)
						{
							foreach (var obj in allowed)
								log.WarnFormat("AddObject: DataObject ({0}) not allowed to be added to Database", obj);
						}
						success = false;
					}
				}
			}
			return success;
		}
		#endregion
		#region Public Save Objects Implementation
		/// <summary>
		/// Saves a DataObject to database if saving is allowed and object is dirty
		/// </summary>
		/// <param name="dataObject">DataObject to Save in database</param>
		/// <returns>True is the DataObject was saved.</returns>
		public bool SaveObject(DataObject dataObject)
		{
			return SaveObject(new [] { dataObject });
		}
		
		/// <summary>
		/// Save DataObjects to database if saving is allowed and object is dirty
		/// </summary>
		/// <param name="dataObjects">DataObjects to Save in database</param>
		/// <returns>True if All DataObjects were saved.</returns>
		public bool SaveObject(IEnumerable<DataObject> dataObjects)
		{
			var success = true;
			foreach (var grp in dataObjects.GroupBy(obj => obj.GetType()))
			{
				var tableHandler = GetTableHandler(grp.Key);
				
				if (tableHandler == null)
				{
					if (log.IsErrorEnabled)
						log.ErrorFormat("SaveObject: DataObject Type ({0}) not registered !", grp.Key.FullName);
					success = false;
					continue;
				}
				
				var objs = grp.Where(obj => obj.Dirty).ToArray();
				var results = SaveObjectImpl(tableHandler, objs);
				var resultsByObjs = results.Select((result, index) => new { Success = result, DataObject = objs[index] })
					.GroupBy(obj => obj.Success);
				
				foreach (var resultGrp in resultsByObjs)
				{
					if (resultGrp.Key)
					{
						// Save in Precache if tablehandler use it
						if (tableHandler.UsesPreCaching)
						{
							var primary = tableHandler.PrimaryKey;
							if (primary != null)
							{
								foreach (var successObj in resultGrp.Select(obj => obj.DataObject))
									tableHandler.SetPreCachedObject(primary.GetValue(successObj), successObj);
							}
						}
					}
					else
					{
						if (log.IsErrorEnabled)
						{
							foreach(var obj in resultGrp)
								log.ErrorFormat("SaveObject: DataObject ({0}) could not be saved into database...", obj.DataObject);
						}
						success = false;
					}
				}
				
				if (tableHandler.HasRelations)
					success &= SaveObjectRelations(tableHandler, grp);				
			}
			return success;
		}
		#endregion
		#region Public Delete Objects Implementation
		/// <summary>
		/// Delete a DataObject from database if deletion is allowed
		/// </summary>
		/// <param name="dataObject">DataObject to Delete from database</param>
		/// <returns>True if the DataObject was deleted.</returns>
		public bool DeleteObject(DataObject dataObject)
		{
			return DeleteObject(new [] { dataObject });
		}
		
		/// <summary>
		/// Delete DataObjects from database if deletion is allowed
		/// </summary>
		/// <param name="dataObjects">DataObjects to Delete from database</param>
		/// <returns>True if All DataObjects were deleted.</returns>
		public bool DeleteObject(IEnumerable<DataObject> dataObjects)
		{
			var success = true;
			foreach (var grp in dataObjects.GroupBy(obj => obj.GetType()))
			{
				var tableHandler = GetTableHandler(grp.Key);
				
				if (tableHandler == null)
				{
					if (log.IsErrorEnabled)
						log.ErrorFormat("DeleteObject: DataObject Type ({0}) not registered !", grp.Key.FullName);
					success = false;
					continue;
				}
				
				foreach (var allowed in grp.GroupBy(item => item.AllowDelete))
				{
					if (allowed.Key)
					{
						var objs = allowed.ToArray();
						var results = DeleteObjectImpl(tableHandler, objs);
						
						var resultsByObjs = results.Select((result, index) => new { Success = result, DataObject = objs[index] })
							.GroupBy(obj => obj.Success);
						
						foreach (var resultGrp in resultsByObjs)
						{
							if (resultGrp.Key)
							{
								// Delete in Precache if tablehandler use it
								if (tableHandler.UsesPreCaching)
								{
									var primary = tableHandler.PrimaryKey;
									if (primary != null)
									{
										foreach (var successObj in resultGrp.Select(obj => obj.DataObject))
											tableHandler.DeletePreCachedObject(primary.GetValue(successObj));
									}
								}
								
								// Success Objects Need to check Relations that should be deleted
								if (tableHandler.HasRelations)
									success &= DeleteObjectRelations(tableHandler, resultGrp.Select(obj => obj.DataObject));
							}
							else
							{
								if (log.IsErrorEnabled)
								{
									foreach(var obj in resultGrp)
										log.ErrorFormat("DeleteObject: DataObject ({0}) could not be deleted from database...", obj.DataObject);
								}
								success = false;
							}
						}
					}
					else
					{
						if (log.IsWarnEnabled)
						{
							foreach (var obj in allowed)
								log.WarnFormat("DeleteObject: DataObject ({0}) not allowed to be deleted from Database", obj);
						}
						success = false;
					}
				}
			}
			return success;
		}
		#endregion
		#region Relation Update Handling
		/// <summary>
		/// Save Relations Objects attached to DataObjects
		/// </summary>
		/// <param name="tableHandler">TableHandler for Source DataObjects Relation</param>
		/// <param name="dataObjects">DataObjects to parse</param>
		/// <returns>True if all Relations were saved</returns>
		protected bool SaveObjectRelations(DataTableHandler tableHandler, IEnumerable<DataObject> dataObjects)
		{
			var success = true;
			foreach (var relation in tableHandler.ElementBindings.Where(bind => bind.Relation != null))
			{
				// Relation Check
				var remoteHandler = GetTableHandler(relation.ValueType);
				if (remoteHandler == null)
				{
					if (log.IsErrorEnabled)
						log.ErrorFormat("SaveObjectRelations: Remote Table for Type ({0}) is not registered !", relation.ValueType.FullName);
					success = false;
					continue;
				}

				// Check For Array Type
				var groups = relation.ValueType.HasElementType
					? dataObjects.Select(obj => new { Source = obj, Enumerable = (IEnumerable<DataObject>)relation.GetValue(obj) })
					.Where(obj => obj.Enumerable != null).Select(obj => obj.Enumerable.Select(rel => new { Local = obj.Source, Remote = rel }))
					.SelectMany(obj => obj).Where(obj => obj.Remote != null).GroupBy(obj => obj.Remote.IsPersisted)
					: dataObjects.Select(obj => new { Local = obj, Remote = (DataObject)relation.GetValue(obj) }).Where(obj => obj.Remote != null).GroupBy(obj => obj.Remote.IsPersisted);
				
				foreach (var grp in groups)
				{
					// Group by object that can be added or saved
					foreach (var allowed in grp.GroupBy(obj => grp.Key ? obj.Remote.Dirty : obj.Remote.AllowAdd))
					{
						if (allowed.Key)
						{
							var objs = allowed.ToArray();
							var results = grp.Key ? SaveObjectImpl(remoteHandler, objs.Select(obj => obj.Remote)) : AddObjectImpl(remoteHandler, objs.Select(obj => obj.Remote));
							
							var resultsByObjs = results.Select((result, index) => new { Success = result, RelObject = objs[index] });
							
							foreach (var resultGrp in resultsByObjs.GroupBy(obj => obj.Success))
							{
								if (resultGrp.Key)
								{
									// Update in Precache if tablehandler use it
									if (remoteHandler.UsesPreCaching)
									{
										var primary = remoteHandler.PrimaryKey;
										if (primary != null)
										{
											foreach (var successObj in resultGrp.Select(obj => obj.RelObject.Remote))
												remoteHandler.SetPreCachedObject(primary.GetValue(successObj), successObj);
										}
									}
								}
								else
								{
									if (log.IsErrorEnabled)
									{
										foreach (var result in resultGrp)
											log.ErrorFormat("SaveObjectRelations: {0} Relation ({1}) of DataObject ({2}) failed for Object ({3})", grp.Key ? "Saving" : "Adding",
											                relation.ValueType, result.RelObject.Local, result.RelObject.Remote);
									}
									success = false;
								}
							}
						}
						else
						{
							// Objects that could not be added can lead to failure
							if (!grp.Key)
							{
								if (log.IsWarnEnabled)
								{
									foreach (var obj in allowed)
										log.WarnFormat("SaveObjectRelations: DataObject ({0}) not allowed to be added to Database", obj);
								}
								success = false;
							}
						}
					}
				}
			}
			return success;
		}
		
		/// <summary>
		/// Delete Relations Objects attached to DataObjects
		/// </summary>
		/// <param name="tableHandler">TableHandler for Source DataObjects Relation</param>
		/// <param name="dataObjects">DataObjects to parse</param>
		/// <returns>True if all Relations were deleted</returns>
		public bool DeleteObjectRelations(DataTableHandler tableHandler, IEnumerable<DataObject> dataObjects)
		{
			var success = true;
			foreach (var relation in tableHandler.ElementBindings.Where(bind => bind.Relation != null && bind.Relation.AutoDelete))
			{
				// Relation Check
				var remoteHandler = GetTableHandler(relation.ValueType);
				if (remoteHandler == null)
				{
					if (log.IsErrorEnabled)
						log.ErrorFormat("DeleteObjectRelations: Remote Table for Type ({0}) is not registered !", relation.ValueType.FullName);
					success = false;
					continue;
				}

				// Check For Array Type
				var groups = relation.ValueType.HasElementType
					? dataObjects.Select(obj => new { Source = obj, Enumerable = (IEnumerable<DataObject>)relation.GetValue(obj) })
					.Where(obj => obj.Enumerable != null).Select(obj => obj.Enumerable.Select(rel => new { Local = obj.Source, Remote = rel }))
					.SelectMany(obj => obj).Where(obj => obj.Remote != null && obj.Remote.IsPersisted)
					: dataObjects.Select(obj => new { Local = obj, Remote = (DataObject)relation.GetValue(obj) }).Where(obj => obj.Remote != null && obj.Remote.IsPersisted);
				
				foreach (var grp in groups.GroupBy(obj => obj.Remote.AllowDelete))
				{
					if (grp.Key)
					{
						var objs = grp.ToArray();
						var results = DeleteObjectImpl(remoteHandler, objs.Select(obj => obj.Remote));
						
						var resultsByObjs = results.Select((result, index) => new { Success = result, RelObject = objs[index] });
						
						foreach (var resultGrp in resultsByObjs.GroupBy(obj => obj.Success))
						{
							if (resultGrp.Key)
							{
								// Delete in Precache if tablehandler use it
								if (remoteHandler.UsesPreCaching)
								{
									var primary = remoteHandler.PrimaryKey;
									if (primary != null)
									{
										foreach (var successObj in resultGrp.Select(obj => obj.RelObject.Remote))
											remoteHandler.DeletePreCachedObject(primary.GetValue(successObj));
									}
								}
							}
							else
							{
								foreach (var result in resultGrp)
								{
									if (log.IsErrorEnabled)
										log.ErrorFormat("DeleteObjectRelations: Deleting Relation ({0}) of DataObject ({1}) failed for Object ({2})",
										                relation.ValueType, result.RelObject.Local, result.RelObject.Remote);
								}
								success = false;
							}
						}
					}
					else
					{
						// Objects that could not be deleted can lead to failure
						if (log.IsWarnEnabled)
						{
							foreach (var obj in grp)
								log.WarnFormat("DeleteObjectRelations: DataObject ({0}) not allowed to be deleted from Database", obj);
						}
						success = false;
					}
				}
				
			}
			return success;
		}
		#endregion
		#region Relation Select/Fill Handling
		/// <summary>
		/// Populate or Refresh Objects Relations
		/// </summary>
		/// <param name="dataObjects">Objects to Populate</param>
		public void FillObjectRelations(IEnumerable<DataObject> dataObjects)
		{
			// Interface Call, force Refresh
			FillObjectRelations(dataObjects, true);
		}
		
		/// <summary>
		/// Populate or Refresh Object Relations
		/// </summary>
		/// <param name="dataObject">Object to Populate</param>
		public void FillObjectRelations(DataObject dataObject)
		{
			// Interface Call, force Refresh
			FillObjectRelations(new [] { dataObject }, true);
		}
		
		/// <summary>
		/// Populate or Refresh Objects Relations
		/// </summary>
		/// <param name="dataObjects">Objects to Populate</param>
		/// <param name="force">Force Refresh even if Autoload is False</param>
		protected virtual void FillObjectRelations(IEnumerable<DataObject> dataObjects, bool force)
		{
			DataObject[] objects = dataObjects.Where(obj => obj != null).ToArray();

			if (objects.Length == 0)
				return;

			List<RelationLoadPlan> relationPlans = new();

			if (objects.Length == 1)
				CreateRelationLoadPlans(objects[0].GetType(), objects, force, relationPlans);
			else
			{
				foreach (IGrouping<Type, DataObject> group in objects.GroupBy(obj => obj.GetType()))
				{
					DataObject[] groupedObjects = group.ToArray();
					CreateRelationLoadPlans(group.Key, groupedObjects, force, relationPlans);
				}
			}

			List<RelationLoadPlan> queriedPlans = new(relationPlans.Count);
			List<SelectQuery> queries = new(relationPlans.Count);

			foreach (RelationLoadPlan plan in relationPlans)
			{
				if (plan.Query == null)
					continue;

				queriedPlans.Add(plan);
				queries.Add(plan.Query);
			}

			if (queriedPlans.Count != 0)
			{
				try
				{
					List<List<DataObject>> resultSets = MultipleSelectObjectsImpl(queries);

					if (resultSets.Count != queriedPlans.Count)
						throw new DatabaseException($"Relation batch returned {resultSets.Count} result sets for {queriedPlans.Count} queries.");

					for (int i = 0; i < queriedPlans.Count; i++)
						queriedPlans[i].QueryResults = resultSets[i];
				}
				catch (Exception batchException)
				{
					if (log.IsWarnEnabled)
						log.WarnFormat("Could not retrieve relation batch; retrying each relation separately.\n{0}", batchException);

					foreach (RelationLoadPlan plan in queriedPlans)
					{
						try
						{
							List<DataObject> results = MultipleSelectObjectsImpl(plan.Query.TableHandler, [plan.Query.WhereClause]).Single();
							plan.QueryResults = results;
						}
						catch (Exception relationException)
						{
							if (log.IsErrorEnabled)
								log.ErrorFormat("Could not retrieve relation {0}.\n{1}", plan.Descriptor.RelationBind.ColumnName, relationException);
						}
					}
				}
			}

			List<DataObject> relatedObjects = new();

			foreach (RelationLoadPlan plan in relationPlans)
			{
				try
				{
					AssignRelations(plan, relatedObjects);
				}
				catch (Exception e)
				{
					if (log.IsErrorEnabled)
						log.ErrorFormat("Could not assign relation {0}\n{1}", plan.Descriptor.RelationBind.ColumnName, e);
				}
			}

			if (relatedObjects.Count != 0)
				FillObjectRelations(relatedObjects, false);

			foreach (DataObject dataObject in objects)
				dataObject.TakeSnapshot();
		}

		private void CreateRelationLoadPlans(Type dataType, DataObject[] dataObjects, bool force, List<RelationLoadPlan> relationPlans)
		{
			string tableName = AttributeUtil.GetTableOrViewName(dataType);

			try
			{
				if (!TableDatasets.TryGetValue(tableName, out DataTableHandler tableHandler))
					throw new DatabaseException(string.Format("Table {0} is not registered for Database Connection...", tableName));

				foreach (ElementBinding relationBind in tableHandler.RelationElementBindings)
				{
					if (!(relationBind.Relation.AutoLoad || force))
						continue;

					try
					{
						RelationDescriptor descriptor = GetRelationDescriptor(tableHandler, relationBind);
						relationPlans.Add(CreateRelationLoadPlan(descriptor, dataObjects));
					}
					catch (Exception relationException)
					{
						if (log.IsErrorEnabled)
							log.ErrorFormat("Could not Retrieve Objects from Relation (Table {0}, Local {1}, Remote Table {2}, Remote {3})\n{4}", tableName,
								relationBind.Relation.LocalField, AttributeUtil.GetTableOrViewName(relationBind.ValueType), relationBind.Relation.RemoteField, relationException);
					}
				}
			}
			catch (Exception exception)
			{
				if (log.IsErrorEnabled)
					log.ErrorFormat("Could not Resolve Relations for Table {0}\n{1}", tableName, exception);
			}
		}

		private RelationDescriptor GetRelationDescriptor(DataTableHandler tableHandler, ElementBinding relationBind)
		{
			return _relationDescriptorCache.GetOrAdd(relationBind, _ =>
			{
				Type relatedType = relationBind.ValueType.HasElementType ? relationBind.ValueType.GetElementType() : relationBind.ValueType;
				string remoteName = AttributeUtil.GetTableOrViewName(relatedType);

				if (!TableDatasets.TryGetValue(remoteName, out DataTableHandler remoteHandler))
					throw new DatabaseException(string.Format("Table {0} is not registered for Database Connection...", remoteName));

				ElementBinding localBind = tableHandler.FieldElementBindings.Single(bind => bind.ColumnName.Equals(relationBind.Relation.LocalField, StringComparison.OrdinalIgnoreCase));
				ElementBinding remoteBind = remoteHandler.FieldElementBindings.Single(bind => bind.ColumnName.Equals(relationBind.Relation.RemoteField, StringComparison.OrdinalIgnoreCase));
				bool remoteFieldIsPrimaryKey = remoteHandler.PrimaryKeys.Length != 0 &&
					remoteHandler.PrimaryKeys.All(primaryKey => primaryKey.ColumnName.Equals(remoteBind.ColumnName, StringComparison.OrdinalIgnoreCase));

				return new RelationDescriptor(relationBind, localBind, remoteBind, remoteHandler, relatedType, remoteFieldIsPrimaryKey);
			});
		}

		private static RelationLoadPlan CreateRelationLoadPlan(RelationDescriptor descriptor, DataObject[] dataObjects)
		{
			RelationLoadPlan plan = new(descriptor, dataObjects);

			if (descriptor.RemoteHandler.UsesPreCaching)
				return plan;

			List<object> localKeys = new(dataObjects.Length);

			if (dataObjects.Length == 1)
			{
				object localValue = descriptor.LocalBind.GetValue(dataObjects[0]);

				if (localValue != null)
					localKeys.Add(localValue);
			}
			else
			{
				HashSet<object> uniqueKeys = new();

				foreach (DataObject dataObject in dataObjects)
				{
					object localValue = descriptor.LocalBind.GetValue(dataObject);

					if (localValue != null && uniqueKeys.Add(localValue))
						localKeys.Add(localValue);
				}
			}

			if (localKeys.Count == 0)
				return plan;

			plan.Query = new SelectQuery(descriptor.RemoteHandler, DB.Column(descriptor.RemoteBind.ColumnName).IsIn(localKeys));
			return plan;
		}

		private static void AssignRelations(RelationLoadPlan plan, List<DataObject> relatedObjects)
		{
			RelationDescriptor descriptor = plan.Descriptor;

			if (descriptor.RemoteHandler.UsesPreCaching)
			{
				AssignPreCachedRelations(plan, relatedObjects);
				return;
			}

			List<DataObject> queryResults = plan.QueryResults ?? [];
			relatedObjects.AddRange(queryResults);

			if (plan.DataObjects.Length == 1)
			{
				AssignRelationValue(descriptor, plan.DataObjects[0], queryResults);
				return;
			}

			Dictionary<object, List<DataObject>> resultsByKey = new();

			foreach (DataObject result in queryResults)
			{
				object remoteValue = descriptor.RemoteBind.GetValue(result);

				if (remoteValue == null)
					continue;

				if (!resultsByKey.TryGetValue(remoteValue, out List<DataObject> matchingResults))
				{
					matchingResults = new List<DataObject>();
					resultsByKey.Add(remoteValue, matchingResults);
				}

				matchingResults.Add(result);
			}

			foreach (DataObject dataObject in plan.DataObjects)
			{
				object localValue = descriptor.LocalBind.GetValue(dataObject);
				IReadOnlyList<DataObject> matchingResults = localValue != null && resultsByKey.TryGetValue(localValue, out List<DataObject> results)
					? results
					: Array.Empty<DataObject>();
				AssignRelationValue(descriptor, dataObject, matchingResults);
			}
		}

		private static void AssignPreCachedRelations(RelationLoadPlan plan, List<DataObject> relatedObjects)
		{
			RelationDescriptor descriptor = plan.Descriptor;

			foreach (DataObject dataObject in plan.DataObjects)
			{
				object localValue = descriptor.LocalBind.GetValue(dataObject);

				if (localValue == null)
				{
					descriptor.RelationBind.SetValue(dataObject, null);
					continue;
				}

				if (descriptor.RemoteFieldIsPrimaryKey)
				{
					DataObject result = descriptor.RemoteHandler.GetPreCachedObject(localValue);

					if (result != null)
						relatedObjects.Add(result);

					AssignSingleRelationValue(descriptor, dataObject, result);
					continue;
				}

				List<DataObject> results = descriptor.RemoteHandler.SearchPreCachedObjects(remoteObject =>
				{
					object remoteValue = descriptor.RemoteBind.GetValue(remoteObject);

					if (remoteValue == null)
						return false;

					if (descriptor.CompareAsString)
						return remoteValue.ToString().Equals(localValue.ToString(), StringComparison.OrdinalIgnoreCase);

					return remoteValue.Equals(localValue);
				}).ToList();

				relatedObjects.AddRange(results);
				AssignRelationValue(descriptor, dataObject, results);
			}
		}

		private static void AssignRelationValue(RelationDescriptor descriptor, DataObject dataObject, IReadOnlyList<DataObject> results)
		{
			if (descriptor.IsArray)
			{
				descriptor.RelationBind.SetValue(dataObject, results.Count == 0 ? null : CastAndToArray(results, descriptor.RelatedType));
				return;
			}

			if (results.Count > 1)
				throw new InvalidOperationException($"Relation {descriptor.RelationBind.ColumnName} returned more than one object.");

			descriptor.RelationBind.SetValue(dataObject, results.Count == 0 ? null : results[0]);
		}

		private static void AssignSingleRelationValue(RelationDescriptor descriptor, DataObject dataObject, DataObject result)
		{
			if (!descriptor.IsArray)
			{
				descriptor.RelationBind.SetValue(dataObject, result);
				return;
			}

			if (result == null)
			{
				descriptor.RelationBind.SetValue(dataObject, null);
				return;
			}

			Array relationArray = Array.CreateInstance(descriptor.RelatedType, 1);
			relationArray.SetValue(result, 0);
			descriptor.RelationBind.SetValue(dataObject, relationArray);
		}

		private sealed class RelationLoadPlan
		{
			public RelationDescriptor Descriptor { get; }
			public DataObject[] DataObjects { get; }
			public SelectQuery Query { get; set; }
			public List<DataObject> QueryResults { get; set; }

			public RelationLoadPlan(RelationDescriptor descriptor, DataObject[] dataObjects)
			{
				Descriptor = descriptor;
				DataObjects = dataObjects;
			}
		}

		private sealed class RelationDescriptor
		{
			public ElementBinding RelationBind { get; }
			public ElementBinding LocalBind { get; }
			public ElementBinding RemoteBind { get; }
			public DataTableHandler RemoteHandler { get; }
			public Type RelatedType { get; }
			public bool IsArray { get; }
			public bool RemoteFieldIsPrimaryKey { get; }
			public bool CompareAsString { get; }

			public RelationDescriptor(ElementBinding relationBind, ElementBinding localBind, ElementBinding remoteBind, DataTableHandler remoteHandler, Type relatedType, bool remoteFieldIsPrimaryKey)
			{
				RelationBind = relationBind;
				LocalBind = localBind;
				RemoteBind = remoteBind;
				RemoteHandler = remoteHandler;
				RelatedType = relatedType;
				IsArray = relationBind.ValueType.HasElementType;
				RemoteFieldIsPrimaryKey = remoteFieldIsPrimaryKey;
				CompareAsString = localBind.ValueType == typeof(string) || remoteBind.ValueType == typeof(string);
			}
		}

		protected sealed class SelectQuery
		{
			public DataTableHandler TableHandler { get; }
			public WhereClause WhereClause { get; }

			public SelectQuery(DataTableHandler tableHandler, WhereClause whereClause)
			{
				TableHandler = tableHandler;
				WhereClause = whereClause;
			}
		}

		private static Array CastAndToArray(IEnumerable<DataObject> source, Type targetType)
		{
			var func = _castToArrayCache.GetOrAdd(targetType, static t =>
			{
				ParameterExpression param = Expression.Parameter(typeof(IEnumerable<DataObject>), "source");
				MethodCallExpression castCall = Expression.Call(typeof(Enumerable), "OfType", [t], param);
				MethodCallExpression toArrayCall = Expression.Call(typeof(Enumerable), "ToArray", [t], castCall);
				Expression<Func<IEnumerable<DataObject>, Array>> lambda = Expression.Lambda<Func<IEnumerable<DataObject>, Array>>(toArrayCall, param);
				return lambda.Compile();
			});

			return func(source);
		}
		#endregion
		#region Public Object Select with Key API
		/// <summary>
		/// Retrieve a DataObject from database based on its primary key value. 
		/// </summary>
		/// <param name="key">Primary Key Value</param>
		/// <returns>Object found or null if not found</returns>
		public TObject FindObjectByKey<TObject>(object key)
			where TObject : DataObject
		{
			return FindObjectsByKey<TObject>(new [] { key }).FirstOrDefault();
		}
		
		/// <summary>
		/// Retrieve a Collection of DataObjects from database based on their primary key values
		/// </summary>
		/// <param name="keys">Collection of Primary Key Values</param>
		/// <returns>Collection of DataObject with primary key matching values</returns>
		public virtual List<TObject> FindObjectsByKey<TObject>(IEnumerable<object> keys)
			where TObject : DataObject
		{
			var tableHandler = GetTableOrViewHandler(typeof(TObject));
			if (tableHandler == null)
			{
				if (log.IsErrorEnabled)
					log.ErrorFormat("FindObjectByKey: DataObject Type ({0}) not registered !", typeof(TObject).FullName);
				
				throw new DatabaseException(string.Format("Table {0} is not registered for Database Connection...", typeof(TObject).FullName));
			}
			
			if (tableHandler.UsesPreCaching)
				return keys.Select(tableHandler.GetPreCachedObject).Cast<TObject>().ToList();
			
			var objs = FindObjectByKeyImpl(tableHandler, keys).Cast<TObject>().ToList();
			
			FillObjectRelations(objs.Where(obj => obj != null), false);
			
			return objs;
		}
		
		/// <summary>
		/// Retrieve a Collection of DataObjects from database based on their primary key values
		/// </summary>
		/// <param name="tableHandler">Table Handler for the DataObjects to Retrieve</param>
		/// <param name="keys">Collection of Primary Key Values</param>
		/// <returns>Collection of DataObject with primary key matching values</returns>
		protected abstract IEnumerable<DataObject> FindObjectByKeyImpl(DataTableHandler tableHandler, IEnumerable<object> keys);
		#endregion

		#region Public Parameterized Query Abstraction
		public TObject SelectObject<TObject>(WhereClause whereClause)
			where TObject : DataObject
		{
			return SelectObjects<TObject>(whereClause).FirstOrDefault();
		}

		public List<TObject> SelectObjects<TObject>(WhereClause whereClause)
			where TObject : DataObject
		{
			return MultipleSelectObjects<TObject>(new[] { whereClause }).First();
		}

		public List<List<TObject>> MultipleSelectObjects<TObject>(IEnumerable<WhereClause> whereClauseBatch)
			where TObject : DataObject
		{
			if (whereClauseBatch == null) throw new ArgumentNullException("Parameter whereClauseBatch may not be null.");

			var tableHandler = GetTableOrViewHandler(typeof(TObject));
			if (tableHandler == null)
			{
				if (log.IsErrorEnabled)
					log.ErrorFormat("SelectObjects: DataObject Type ({0}) not registered !", typeof(TObject).FullName);

				throw new DatabaseException(string.Format("Table {0} is not registered for Database Connection...", typeof(TObject).FullName));
			}

			var objs = MultipleSelectObjectsImpl(tableHandler, whereClauseBatch).Select(res => res.OfType<TObject>().ToList()).ToList();

			FillObjectRelations(objs.SelectMany(obj => obj), false);

			return objs;
		}
		#endregion
		
		#region Public Object Select All API
		public List<TObject> SelectAllObjects<TObject>()
			where TObject : DataObject
		{
			var tableHandler = GetTableOrViewHandler(typeof(TObject));
			if (tableHandler == null)
			{
				if (log.IsErrorEnabled)
					log.ErrorFormat("SelectAllObjects: DataObject Type ({0}) not registered !", typeof(TObject).FullName);

				throw new DatabaseException(string.Format("Table {0} is not registered for Database Connection...", typeof(TObject).FullName));
			}

			if (tableHandler.UsesPreCaching)
				return tableHandler.SearchPreCachedObjects(obj => obj != null).OfType<TObject>().ToList();

			var dataObjects = MultipleSelectObjectsImpl(tableHandler, new[] { WhereClause.Empty }).Single().OfType<TObject>().ToList();

			FillObjectRelations(dataObjects, false);

			return dataObjects;
		}
		#endregion
		
		#region Public API
		/// <summary>
		/// Gets the number of objects in a given table in the database.
		/// </summary>
		/// <typeparam name="TObject">the type of objects to retrieve</typeparam>
		/// <returns>a positive integer representing the number of objects; zero if no object exists</returns>
		public int GetObjectCount<TObject>()
			where TObject : DataObject
		{
			return GetObjectCount<TObject>(string.Empty);
		}

		/// <summary>
		/// Gets the number of objects in a given table in the database based on a given set of criteria. (where clause)
		/// </summary>
		/// <typeparam name="TObject">the type of objects to retrieve</typeparam>
		/// <param name="whereExpression">the where clause to filter object count on</param>
		/// <returns>a positive integer representing the number of objects that matched the given criteria; zero if no such objects existed</returns>
		public int GetObjectCount<TObject>(string whereExpression)
			where TObject : DataObject
		{
			return GetObjectCountImpl<TObject>(whereExpression);
		}

		/// <summary>
		/// Register Data Object Type if not already Registered
		/// </summary>
		/// <param name="dataObjectType">DataObject Type</param>
		public virtual void RegisterDataObject(Type dataObjectType)
		{
			var tableName = AttributeUtil.GetTableOrViewName(dataObjectType);
			if (TableDatasets.ContainsKey(tableName))
				return;
			
			var dataTableHandler = new DataTableHandler(dataObjectType);
			TableDatasets.Add(tableName, dataTableHandler);
		}

		/// <summary>
		/// escape the strange character from string
		/// </summary>
		/// <param name="rawInput">the string</param>
		/// <returns>the string with escaped character</returns>
		public abstract string Escape(string rawInput);
		
		/// <summary>
		/// Execute a Raw Non-Query on the Database
		/// </summary>
		/// <param name="rawQuery">Raw Command</param>
		/// <returns>True if the Command succeeded</returns>
		public virtual bool ExecuteNonQuery(string rawQuery)
		{
			throw new NotImplementedException();
		}

		#endregion

		#region Implementation
		/// <summary>
		/// Adds new DataObjects to the database.
		/// </summary>
		/// <param name="dataObjects">DataObjects to add to the database</param>
		/// <param name="tableHandler">Table Handler for the DataObjects Collection</param>
		/// <returns>True if objects were added successfully; false otherwise</returns>
		protected abstract IEnumerable<bool> AddObjectImpl(DataTableHandler tableHandler, IEnumerable<DataObject> dataObjects);

		/// <summary>
		/// Saves Persisted DataObjects into Database
		/// </summary>
		/// <param name="dataObjects">DataObjects to Save</param>
		/// <param name="tableHandler">Table Handler for the DataObjects Collection</param>
		/// <returns>True if objects were saved successfully; false otherwise</returns>
		protected abstract IEnumerable<bool> SaveObjectImpl(DataTableHandler tableHandler, IEnumerable<DataObject> dataObjects);

		/// <summary>
		/// Deletes DataObjects from the database.
		/// </summary>
		/// <param name="dataObjects">DataObjects to delete from the database</param>
		/// <param name="tableHandler">Table Handler for the DataObjects Collection</param>
		/// <returns>True if objects were deleted successfully; false otherwise</returns>
		protected abstract IEnumerable<bool> DeleteObjectImpl(DataTableHandler tableHandler, IEnumerable<DataObject> dataObjects);

		/// <summary>
		/// Retrieve a Collection of DataObjects Sets from database filtered by Parametrized Where Expression
		/// </summary>
		/// <param name="tableHandler">Table Handler for these DataObjects</param>
		/// <param name="whereExpression">Parametrized Where Expression</param>
		/// <param name="parameters">Parameters for filtering</param>
		/// <param name="isolation">Isolation Level</param>
		/// <returns>Collection of DataObjects Sets matching Parametrized Where Expression</returns>
		protected abstract List<List<DataObject>> SelectObjectsImpl(DataTableHandler tableHandler, string whereExpression, IEnumerable<IEnumerable<QueryParameter>> parameters, Transaction.EIsolationLevel isolation);

		protected abstract List<List<DataObject>> MultipleSelectObjectsImpl(DataTableHandler tableHandler, IEnumerable<WhereClause> whereClauseBatch);

		/// <summary>
		/// Executes heterogeneous selects in result-set order. SQL providers override this
		/// to use one command and one database round trip; other providers retain the
		/// sequential fallback.
		/// </summary>
		protected virtual List<List<DataObject>> MultipleSelectObjectsImpl(IReadOnlyList<SelectQuery> queries)
		{
			List<List<DataObject>> resultSets = new(queries.Count);

			foreach (SelectQuery query in queries)
				resultSets.Add(MultipleSelectObjectsImpl(query.TableHandler, [query.WhereClause]).Single());

			return resultSets;
		}

		/// <summary>
		/// Gets the number of objects in a given table in the database based on a given set of criteria. (where clause)
		/// </summary>
		/// <typeparam name="TObject">the type of objects to retrieve</typeparam>
		/// <param name="whereExpression">the where clause to filter object count on</param>
		/// <returns>a positive integer representing the number of objects that matched the given criteria; zero if no such objects existed</returns>
		protected abstract int GetObjectCountImpl<TObject>(string whereExpression)
			where TObject : DataObject;
		#endregion

		#region Cache
		/// <summary>
		/// Selects object from the database and updates or adds entry in the pre-cache.
		/// </summary>
		/// <typeparam name="TObject">DataObject Type to Query</typeparam>
		/// <param name="key">Key to Update</param>
		/// <returns>True if Object was found with given key</returns>
		public bool UpdateInCache<TObject>(object key)
			where TObject : DataObject
		{
			return UpdateObjsInCache<TObject>(new [] { key });
		}
		
		/// <summary>
		/// Selects objects from the database and updates or adds entries in the pre-cache.
		/// </summary>
		/// <typeparam name="TObject">DataObject Type to Query</typeparam>
		/// <param name="keys">Key Collection to Update</param>
		/// <returns>True if All Objects were found with given keys</returns>
		public bool UpdateObjsInCache<TObject>(IEnumerable<object> keys)
			where TObject : DataObject
		{
			var tableHandler = GetTableOrViewHandler(typeof(TObject));
			if (tableHandler == null)
			{
				if (log.IsErrorEnabled)
					log.ErrorFormat("UpdateInCache: DataObject Type ({0}) not registered !", typeof(TObject).FullName);
				
				throw new DatabaseException(string.Format("Table {0} is not registered for Database Connection...", typeof(TObject).FullName));
			}
			
			var keysArray = keys.ToArray();
			var objs = FindObjectByKeyImpl(tableHandler, keysArray);
			var objsByKey = objs.Select((obj, i) => new { Key = keysArray[i], DataObject = obj });
			
			var success = true;
			foreach (var obj in objsByKey)
			{
				if (obj.DataObject != null)
					tableHandler.SetPreCachedObject(obj.Key, obj.DataObject);
				else
					success = false;
			}
			
			return success;
		}

		#endregion

		#region Factory

		public static IObjectDatabase GetObjectDatabase(EConnectionType connectionType, string connectionString)
		{
			if (connectionType == EConnectionType.DATABASE_MYSQL)
				return new MySqlObjectDatabase(connectionString);
			if (connectionType == EConnectionType.DATABASE_SQLITE)
				return new SqliteObjectDatabase(connectionString);

			return null;
		}

		#endregion
	}
}
