﻿#region License
// TableDependency, SqlTableDependency
// Copyright (c) 2015-2018 Christian Del Bianco. All rights reserved.
//
// Permission is hereby granted, free of charge, to any person
// obtaining a copy of this software and associated documentation
// files (the "Software"), to deal in the Software without
// restriction, including without limitation the rights to use,
// copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following
// conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
// OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
// OTHER DEALINGS IN THE SOFTWARE.
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TableDependency.Abstracts;
using TableDependency.Delegates;
using TableDependency.Enums;
using TableDependency.EventArgs;
using TableDependency.Exceptions;
using TableDependency.Messages;
using TableDependency.SqlClient.Enumerations;
using TableDependency.SqlClient.EventArgs;
using TableDependency.SqlClient.Exceptions;
using TableDependency.SqlClient.Extensions;
using TableDependency.SqlClient.Messages;
using TableDependency.SqlClient.Resources;
using TableDependency.SqlClient.Utilities;
using TableDependency.Utilities;
#endregion

namespace TableDependency.SqlClient
{
    /// <summary>
    /// SqlTableDependency class.
    /// </summary>
    public class SqlTableDependency<T> : TableDependency<T> where T : class, new()
    {
        #region Private variables

        protected Guid ConversationHandle;
        protected const string StartMessageTemplate = "{0}/StartMessage/{1}";
        protected const string EndMessageTemplate = "{0}/EndMessage";

        #endregion

        #region Properties

        /// <summary>
        /// Set if notification must contains the old value too.
        /// </summary>
        /// <value>
        ///   <c>true</c> if [include modified values]; otherwise, <c>false</c>.
        /// </value>
        public bool IncludeOldValues { get; }

        /// <summary>
        /// Gets or sets a value indicating whether activate database loging and event viewer loging.
        /// </summary>
        /// <remarks>
        /// Only a member of the sysadmin fixed server role or a user with ALTER TRACE permissions can use it.
        /// </remarks>
        /// <value>
        /// <c>true</c> if [activate database loging]; otherwise, <c>false</c>.
        /// </value>
        public bool ActivateDatabaseLoging { get; set; }

        /// <summary>
        /// Specifies the owner of the service to the specified database user.
        /// When a new service is created it is owned by the principal specified in the AUTHORIZATION clause. Server, database, and schema names cannot be specified. The service_name must be a valid sysname.
        /// When the current user is dbo or sa, owner_name may be the name of any valid user or role.
        /// Otherwise, owner_name must be the name of the current user, the name of a user that the current user has IMPERSONATE permission for, or the name of a role to which the current user belongs.
        /// </summary>
        public string ServiceAuthorization { get; set; }

        /// <summary>
        /// Specifies the SQL Server database user account under which the activation stored procedure runs.
        /// SQL Server must be able to check the permissions for this user at the time that the queue activates the stored procedure. For aWindows domain user, the server must be connected to the domain
        /// when the procedure is activated or when activation fails.For a SQL Server user, Service Broker always checks the permissions.EXECUTE AS SELF means that the stored procedure executes as the current user.
        /// </summary>
        public string QueueExecuteAs { get; set; } = "SELF";

        #endregion

        #region Events

        /// <summary>
        /// Occurs when an error happen during listening for changes on monitored table.
        /// </summary>
        public override event ErrorEventHandler OnError;

        /// <summary>
        /// Occurs when the table content has been changed with an update, insert or delete operation.
        /// </summary>
        public override event ChangedEventHandler<T> OnChanged;

        /// <summary>
        /// Occurs when an status changes happen.
        /// </summary>
        public override event StatusEventHandler OnStatusChanged;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="SqlTableDependency{T}" /> class.
        /// </summary>
        /// <param name="connectionString">The connection string.</param>
        /// <param name="tableName">Name of the table.</param>
        /// <param name="schemaName">Name of the schema.</param>
        /// <param name="mapper">The model to database table column mapper.</param>
        /// <param name="updateOf">List of columns that need to monitor for changing on order to receive notifications.</param>
        /// <param name="filter">The filter condition translated in WHERE.</param>
        /// <param name="notifyOn">The notify on Insert, Delete, Update operation.</param>
        /// <param name="executeUserPermissionCheck">if set to <c>true</c> [skip user permission check].</param>
        /// <param name="includeOldValues">if set to <c>true</c> [include old values].</param>
        public SqlTableDependency(
            string connectionString,
            string tableName = null,
            string schemaName = null,            
            IModelToTableMapper<T> mapper = null,
            IUpdateOfModel<T> updateOf = null,
            ITableDependencyFilter filter = null,
            DmlTriggerType notifyOn = DmlTriggerType.All,
            bool executeUserPermissionCheck = true,
            bool includeOldValues = false) : base(connectionString, tableName, schemaName, mapper, updateOf, filter, notifyOn, executeUserPermissionCheck)
        {
            this.IncludeOldValues = includeOldValues;
        }

        #endregion

        #region Public methods

        /// <summary>
        /// Starts monitoring table's content changes.
        /// </summary>
        /// <param name="timeOut">The WAITFOR timeout in seconds.</param>
        /// <param name="watchDogTimeOut">The WATCHDOG timeout in seconds.</param>
        /// <returns></returns>
        /// <exception cref="NoSubscriberException"></exception>
        /// <exception cref="TableDependency.Exceptions.NoSubscriberException"></exception>
        public override void Start(int timeOut = 120, int watchDogTimeOut = 180)
        {
            if (OnChanged == null) throw new NoSubscriberException();

            var onChangedSubscribedList = OnChanged?.GetInvocationList();
            var onErrorSubscribedList = OnError?.GetInvocationList();
            var onStatusChangedSubscribedList = OnStatusChanged?.GetInvocationList();

            this.NotifyListenersAboutStatus(onStatusChangedSubscribedList, TableDependencyStatus.Starting);

            base.Start(timeOut, watchDogTimeOut);

            _cancellationTokenSource = new CancellationTokenSource();

            _task = Task.Factory.StartNew(() =>
                WaitForNotifications(
                    _cancellationTokenSource.Token,
                    onChangedSubscribedList,
                    onErrorSubscribedList,
                    onStatusChangedSubscribedList,
                    timeOut,
                    watchDogTimeOut),
                _cancellationTokenSource.Token);

            this.WriteTraceMessage(TraceLevel.Info, $"Waiting for receiving {_tableName}'s records change notifications.");
        }

        #endregion

        #region protected virtual methods

        protected virtual string Spacer(int numberOrSpaces)
        {
            var stringBuilder = new StringBuilder();
            for (int i = 1; i <= numberOrSpaces; i++) stringBuilder.Append(' ');
            return stringBuilder.ToString();
        }

        protected override RecordChangedEventArgs<T> GetRecordChangedEventArgs(MessagesBag messagesBag)
        {
            return new SqlRecordChangedEventArgs<T>(
                messagesBag,
                _mapper,
                _userInterestedColumns,
                _server,
                _database,
                _dataBaseObjectsNamingConvention,
                base.CultureInfo,
                this.IncludeOldValues);
        }

        protected override string GetDataBaseName()
        {
            var sqlConnectionStringBuilder = new SqlConnectionStringBuilder(_connectionString);
            return sqlConnectionStringBuilder.InitialCatalog;
        }

        protected override string GetServerName()
        {
            var sqlConnectionStringBuilder = new SqlConnectionStringBuilder(_connectionString);
            return sqlConnectionStringBuilder.DataSource;
        }

        protected override string GetTableName(string tableName)
        {
            if (!string.IsNullOrWhiteSpace(tableName))
            {
                return tableName.Replace("[", string.Empty).Replace("]", string.Empty);
            }

            var tableNameFromDataAnotation = GetTableNameFromDataAnnotation();
            return !string.IsNullOrWhiteSpace(tableNameFromDataAnotation) ? tableNameFromDataAnotation : typeof(T).Name;
        }

        protected override string GetSchemaName(string schemaName)
        {
            if (!string.IsNullOrWhiteSpace(schemaName))
            {
                return schemaName.Replace("[", string.Empty).Replace("]", string.Empty);
            }

            var schemaNameFromDataAnnotation = GetSchemaNameFromDataAnnotation();
            return !string.IsNullOrWhiteSpace(schemaNameFromDataAnnotation) ? schemaNameFromDataAnnotation : "dbo";
        }

        protected virtual int GetSchemaId()
        {
            using (var sqlConnection = new SqlConnection(_connectionString))
            {
                sqlConnection.Open();
                using (var sqlCommand = sqlConnection.CreateCommand())
                {
                    sqlCommand.CommandText = $"SELECT [schema_id] FROM [sys].[schemas] WITH (NOLOCK) WHERE [name] = '{_schemaName}'";
                    return (int)sqlCommand.ExecuteScalar();
                }
            }
        }

        protected virtual SqlServerVersion GetSqlServerVersion()
        {
            var sqlConnection = new SqlConnection(_connectionString);

            try
            {
                sqlConnection.Open();

                var serverVersion = sqlConnection.ServerVersion;
                if (string.IsNullOrWhiteSpace(serverVersion)) return SqlServerVersion.Unknown;

                var serverVersionDetails = serverVersion.Split(new[] { "." }, StringSplitOptions.None);
                var versionNumber = int.Parse(serverVersionDetails[0]);

                if (versionNumber < 8) return SqlServerVersion.Unknown;
                if (versionNumber == 8) return SqlServerVersion.SqlServer2000;
                if (versionNumber == 9) return SqlServerVersion.SqlServer2005;
                if (versionNumber == 10) return SqlServerVersion.SqlServer2008;
                if (versionNumber == 11) return SqlServerVersion.SqlServer2012;
            }
            catch
            {
                throw new SqlServerVersionNotSupportedException();
            }
            finally
            {
                sqlConnection.Close();
            }

            return SqlServerVersion.SqlServerLatest;
        }

        protected override IEnumerable<ColumnInfo> GetTableColumnsList()
        {
            var columnsList = new List<ColumnInfo>();

            using (var sqlConnection = new SqlConnection(_connectionString))
            {
                sqlConnection.Open();
                using (var sqlCommand = sqlConnection.CreateCommand())
                {
                    sqlCommand.CommandText = string.Format(SqlScripts.InformationSchemaColumns, _schemaName, _tableName);
                    var reader = sqlCommand.ExecuteReader();
                    while (reader.Read())
                    {
                        var name = reader["COLUMN_NAME"].ToString();
                        var type = reader["DATA_TYPE"].ToString().ConvertNumericType();
                        var size = this.ComputeSize(
                            type,
                            reader.GetSafeString(reader.GetOrdinal("CHARACTER_MAXIMUM_LENGTH")),
                            reader.GetSafeString(reader.GetOrdinal("NUMERIC_PRECISION")),
                            reader.GetSafeString(reader.GetOrdinal("NUMERIC_SCALE")),
                            reader.GetSafeString(reader.GetOrdinal("DATETIME_PRECISION")));

                        columnsList.Add(new ColumnInfo(name, type, size));
                    }
                }
            }

            return columnsList;
        }

        protected virtual bool CheckIfDatabaseObjectExists()
        {
            bool result;

            using (var sqlConnection = new SqlConnection(_connectionString))
            {
                sqlConnection.Open();
                var sqlCommand = new SqlCommand($"SELECT COUNT(*) FROM sys.service_queues WITH (NOLOCK) WHERE name = N'{_dataBaseObjectsNamingConvention}';", sqlConnection);
                result = (int)sqlCommand.ExecuteScalar() > 0;
                sqlConnection.Close();
            }

            return result;
        }

        protected override IList<string> CreateDatabaseObjects(int timeOut, int watchDogTimeOut)
        {
            IList<string> processableMessages;

            var interestedColumns = _userInterestedColumns as ColumnInfo[] ?? _userInterestedColumns.ToArray();

            if (this.CheckIfDatabaseObjectExists() == false)
            {
                var columnsForUpdateOf = _updateOf != null ? string.Join(" OR ", _updateOf.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.CurrentCultureIgnoreCase).Select(c => $"UPDATE([{c}])").ToList()) : null;
                processableMessages = this.CreateSqlServerDatabaseObjects(interestedColumns, columnsForUpdateOf, watchDogTimeOut);
            }
            else
            {
                throw new DbObjectsWithSameNameException(_dataBaseObjectsNamingConvention);
            }

            return processableMessages;
        }

        protected override string GetBaseObjectsNamingConvention()
        {
            var name = $"{_schemaName}_{_tableName}";
            return $"{name}_{Guid.NewGuid()}";
        }

        protected override void DropDatabaseObjects()
        {
            using (var sqlConnection = new SqlConnection(_connectionString))
            {
                sqlConnection.Open();

                using (var sqlCommand = sqlConnection.CreateCommand())
                {
                    var dropMessages = string.Join(Environment.NewLine, _processableMessages.Select(pm => string.Format("IF EXISTS (SELECT * FROM sys.service_message_types WITH (NOLOCK) WHERE name = N'{0}') DROP MESSAGE TYPE [{0}];", pm)));
                    var dropAllScript = this.PrepareScriptDropAll(dropMessages);

                    sqlCommand.CommandType = CommandType.Text;
                    sqlCommand.CommandText = dropAllScript;
                    sqlCommand.ExecuteNonQuery();
                }
            }

            this.WriteTraceMessage(TraceLevel.Info, "DropDatabaseObjects method executed.");
        }

        protected override void CheckRdbmsDependentImplementation()
        {
            this.CheckIfServiceBrokerIsEnabled();

            var sqlVersion = this.GetSqlServerVersion();
            if (sqlVersion < SqlServerVersion.SqlServer2008) throw new SqlServerVersionNotSupportedException(sqlVersion);
        }

        protected virtual string CreateWhereCondifition(bool prependSpace = false)
        {
            var where = string.Empty;

            var filter = _filter?.Translate();
            if (!string.IsNullOrWhiteSpace(filter))
            {
                where = (prependSpace ? " " : string.Empty) + "WHERE " + filter;
            }

            return where;
        }

        protected virtual string PrepareInsertIntoTableVariableForUpdateUserChange(ColumnInfo[] userInterestedColumns, string columnsForUpdateOf)
        {
            var exceptStatement = this.PrepareExceptStatement(userInterestedColumns);

            var columnsForSelectFromTableVariable = userInterestedColumns.Select(c =>
            {
                var column = $"[{c.Name}]";
                if (this.IncludeOldValues) column += $", (SELECT [{c.Name}] FROM DELETED)";
                return column;
            });

            var columnsList = string.Join(", ", columnsForSelectFromTableVariable.ToList());

            var scriptForInsertInTableVariable = !string.IsNullOrEmpty(columnsForUpdateOf)
                ? string.Format(SqlScripts.InsertInTableVariableConsideringUpdateOf, columnsForUpdateOf, columnsList, ChangeType.Update, exceptStatement)
                : string.Format(SqlScripts.InsertInTableVariable, columnsList, ChangeType.Update, exceptStatement);

            return scriptForInsertInTableVariable;
        }

        protected virtual IList<string> CreateSqlServerDatabaseObjects(
            IEnumerable<ColumnInfo> userInterestedColumns, 
            string columnsForUpdateOf, 
            int watchDogTimeOut)
        {
            var processableMessages = new List<string>();
            var tableColumns = userInterestedColumns as IList<ColumnInfo> ?? userInterestedColumns.ToList();

            var columnsForTableVariable = this.PrepareColumnListForTableVariable(tableColumns);

            using (var sqlConnection = new SqlConnection(_connectionString))
            {
                sqlConnection.Open();

                using (var transaction = sqlConnection.BeginTransaction())
                {
                    var sqlCommand = new SqlCommand($"SELECT COUNT(*) FROM sys.service_queues WITH (NOLOCK) WHERE name LIKE N'%{_dataBaseObjectsNamingConvention}%';", sqlConnection, transaction);
                    if ((int)sqlCommand.ExecuteScalar() > 0) throw new DbObjectsWithSameNameException(_dataBaseObjectsNamingConvention);

                    // Messages
                    var startMessageInsert = string.Format(StartMessageTemplate, _dataBaseObjectsNamingConvention, ChangeType.Insert);
                    sqlCommand.CommandText = $"CREATE MESSAGE TYPE [{startMessageInsert}] VALIDATION = NONE;";
                    sqlCommand.ExecuteNonQuery();
                    this.WriteTraceMessage(TraceLevel.Verbose, $"Message {startMessageInsert} created.");
                    processableMessages.Add(startMessageInsert);

                    var startMessageUpdate = string.Format(StartMessageTemplate, _dataBaseObjectsNamingConvention, ChangeType.Update);
                    sqlCommand.CommandText = $"CREATE MESSAGE TYPE [{startMessageUpdate}] VALIDATION = NONE;";
                    sqlCommand.ExecuteNonQuery();
                    this.WriteTraceMessage(TraceLevel.Verbose, $"Message {startMessageUpdate} created.");
                    processableMessages.Add(startMessageUpdate);

                    var startMessageDelete = string.Format(StartMessageTemplate, _dataBaseObjectsNamingConvention, ChangeType.Delete);
                    sqlCommand.CommandText = $"CREATE MESSAGE TYPE [{startMessageDelete}] VALIDATION = NONE;";
                    sqlCommand.ExecuteNonQuery();
                    this.WriteTraceMessage(TraceLevel.Verbose, $"Message {startMessageDelete} created.");
                    processableMessages.Add(startMessageDelete);

                    var interestedColumns = userInterestedColumns as ColumnInfo[] ?? tableColumns.ToArray();
                    foreach (var userInterestedColumn in interestedColumns)
                    {
                        var message = $"{_dataBaseObjectsNamingConvention}/{userInterestedColumn.Name}";
                        sqlCommand.CommandText = $"CREATE MESSAGE TYPE [{message}] VALIDATION = NONE;";
                        sqlCommand.ExecuteNonQuery();
                        this.WriteTraceMessage(TraceLevel.Verbose, $"Message {message} created.");
                        processableMessages.Add(message);

                        if (this.IncludeOldValues)
                        {
                            message = $"{_dataBaseObjectsNamingConvention}/{userInterestedColumn.Name}/old";
                            sqlCommand.CommandText = $"CREATE MESSAGE TYPE [{message}] VALIDATION = NONE;";
                            sqlCommand.ExecuteNonQuery();
                            this.WriteTraceMessage(TraceLevel.Verbose, $"Message {message} created.");
                            processableMessages.Add(message);
                        }
                    }

                    var endMessage = string.Format(EndMessageTemplate, _dataBaseObjectsNamingConvention);
                    sqlCommand.CommandText = $"CREATE MESSAGE TYPE [{endMessage}] VALIDATION = NONE;";
                    sqlCommand.ExecuteNonQuery();
                    this.WriteTraceMessage(TraceLevel.Verbose, $"Message {endMessage} created.");
                    processableMessages.Add(endMessage);

                    // Contract
                    var contractBody = string.Join("," + Environment.NewLine, processableMessages.Select(message => $"[{message}] SENT BY INITIATOR"));
                    sqlCommand.CommandText = $"CREATE CONTRACT [{_dataBaseObjectsNamingConvention}] ({contractBody})";
                    sqlCommand.ExecuteNonQuery();
                    this.WriteTraceMessage(TraceLevel.Verbose, $"Contract {_dataBaseObjectsNamingConvention} created.");

                    // Queues
                    sqlCommand.CommandText = $"CREATE QUEUE [{_schemaName}].[{_dataBaseObjectsNamingConvention}_Receiver] WITH STATUS = ON, RETENTION = OFF, POISON_MESSAGE_HANDLING (STATUS = OFF);";
                    sqlCommand.ExecuteNonQuery();
                    this.WriteTraceMessage(TraceLevel.Verbose, $"Queue {_dataBaseObjectsNamingConvention}_Receiver created.");

                    sqlCommand.CommandText = $"CREATE QUEUE [{_schemaName}].[{_dataBaseObjectsNamingConvention}_Sender] WITH STATUS = ON, RETENTION = OFF, POISON_MESSAGE_HANDLING (STATUS = OFF);";
                    sqlCommand.ExecuteNonQuery();
                    this.WriteTraceMessage(TraceLevel.Verbose, $"Queue {_dataBaseObjectsNamingConvention}_Sender created.");

                    // Services
                    sqlCommand.CommandText = string.IsNullOrWhiteSpace(this.ServiceAuthorization)
                        ? $"CREATE SERVICE [{_dataBaseObjectsNamingConvention}_Sender] ON QUEUE [{_schemaName}].[{_dataBaseObjectsNamingConvention}_Sender];"
                        : $"CREATE SERVICE [{_dataBaseObjectsNamingConvention}_Sender] AUTHORIZATION [{this.ServiceAuthorization}] ON QUEUE [{_schemaName}].[{_dataBaseObjectsNamingConvention}_Sender];";
                    sqlCommand.ExecuteNonQuery();
                    this.WriteTraceMessage(TraceLevel.Verbose, $"Service broker {_dataBaseObjectsNamingConvention}_Sender created.");

                    sqlCommand.CommandText = string.IsNullOrWhiteSpace(this.ServiceAuthorization)
                        ? $"CREATE SERVICE [{_dataBaseObjectsNamingConvention}_Receiver] ON QUEUE [{_schemaName}].[{_dataBaseObjectsNamingConvention}_Receiver] ([{_dataBaseObjectsNamingConvention}]);"
                        : $"CREATE SERVICE [{_dataBaseObjectsNamingConvention}_Receiver] AUTHORIZATION [{this.ServiceAuthorization}] ON QUEUE [{_schemaName}].[{_dataBaseObjectsNamingConvention}_Receiver] ([{_dataBaseObjectsNamingConvention}]);";
                    sqlCommand.ExecuteNonQuery();
                    this.WriteTraceMessage(TraceLevel.Verbose, $"Service broker {_dataBaseObjectsNamingConvention}_Receiver created.");

                    // Activation Store Procedure
                    var dropMessages = string.Join(Environment.NewLine, processableMessages.Select(pm => string.Format("IF EXISTS (SELECT * FROM sys.service_message_types WITH (NOLOCK) WHERE name = N'{0}') DROP MESSAGE TYPE [{0}];", pm)));
                    var dropAllScript = this.PrepareScriptDropAll(dropMessages);
                    sqlCommand.CommandText = this.PrepareScriptProcedureQueueActivation(dropAllScript);
                    sqlCommand.ExecuteNonQuery();
                    this.WriteTraceMessage(TraceLevel.Verbose, $"Procedure {_dataBaseObjectsNamingConvention} created.");

                    // Begin conversation
                    this.ConversationHandle = this.BeginConversation(sqlCommand);
                    this.WriteTraceMessage(TraceLevel.Verbose, $"Conversation with handler {this.ConversationHandle} started.");

                    // Trigger
                    var declareVariableStatement = this.PrepareDeclareVariableStatement(interestedColumns);
                    var selectForSetVariablesStatement = this.PrepareSelectForSetVariables(interestedColumns);
                    var sendInsertConversationStatements = this.PrepareSendConversation(ChangeType.Insert, interestedColumns);
                    var sendUpdatedConversationStatements = this.PrepareSendConversation(ChangeType.Update, interestedColumns);
                    var sendDeletedConversationStatements = this.PrepareSendConversation(ChangeType.Delete, interestedColumns);

                    sqlCommand.CommandText = string.Format(
                        SqlScripts.CreateTrigger,
                        _dataBaseObjectsNamingConvention,
                        $"[{_schemaName}].[{_tableName}]",
                        columnsForTableVariable,
                        this.PrepareColumnListForSelectFromTableVariable(tableColumns),
                        this.PrepareInsertIntoTableVariableForUpdateUserChange(interestedColumns, columnsForUpdateOf),
                        declareVariableStatement,
                        selectForSetVariablesStatement,
                        sendInsertConversationStatements,
                        sendUpdatedConversationStatements,
                        sendDeletedConversationStatements,
                        ChangeType.Insert,
                        ChangeType.Update,
                        ChangeType.Delete,
                        string.Join(", ", this.GetDmlTriggerType(_dmlTriggerType)),
                        this.CreateWhereCondifition(),
                        this.PrepareTriggerLogScript(),
                        this.ActivateDatabaseLoging ? " WITH LOG" : string.Empty);

                    sqlCommand.ExecuteNonQuery();
                    this.WriteTraceMessage(TraceLevel.Verbose, $"Trigger {_dataBaseObjectsNamingConvention} created.");

                    // Associate Activation Store Procedure to sender queue
                    sqlCommand.CommandText = $"ALTER QUEUE [{_schemaName}].[{_dataBaseObjectsNamingConvention}_Sender] WITH ACTIVATION (PROCEDURE_NAME = [{_schemaName}].[{_dataBaseObjectsNamingConvention}_QueueActivationSender], MAX_QUEUE_READERS = 1, EXECUTE AS {this.QueueExecuteAs.ToUpper()}, STATUS = ON);";
                    sqlCommand.ExecuteNonQuery();

                    // Persist all objects
                    transaction.Commit();
                }

                this.WriteTraceMessage(TraceLevel.Info, $"Database objects created with naming {_dataBaseObjectsNamingConvention}.");
            }

            return processableMessages;
        }

        protected virtual Guid BeginConversation(SqlCommand sqlCommand)
        {
            sqlCommand.CommandText = $"DECLARE @h AS UNIQUEIDENTIFIER; BEGIN DIALOG CONVERSATION @h FROM SERVICE [{_dataBaseObjectsNamingConvention}_Sender] TO SERVICE '{_dataBaseObjectsNamingConvention}_Receiver' ON CONTRACT [{_dataBaseObjectsNamingConvention}] WITH ENCRYPTION = OFF; SELECT @h;";
            var conversationHandler = (Guid)sqlCommand.ExecuteScalar();
            if (conversationHandler == Guid.Empty) throw new ServiceBrokerConversationHandlerInvalidException();

            return conversationHandler;
        }

        protected virtual string PrepareTriggerLogScript()
        {
            if (this.ActivateDatabaseLoging == false) return string.Empty;

            return
                Environment.NewLine + Environment.NewLine + "DECLARE @LogMessage varchar(255);" + Environment.NewLine +
                $"SET @LogMessage = 'SqlTableDependency: Message for ' + @dmlType + ' operation added in Queue [{_dataBaseObjectsNamingConvention}].'" + Environment.NewLine +
                "RAISERROR(@LogMessage, 10, 1) WITH LOG;";
        }

        protected virtual string PrepareScriptProcedureQueueActivation(string dropAllScript)
        {
            var script = string.Format(SqlScripts.CreateProcedureQueueActivation, _dataBaseObjectsNamingConvention, dropAllScript, _schemaName);
            return this.ActivateDatabaseLoging ? script : this.RemoveLogOperations(script);
        }

        protected virtual string PrepareScriptDropAll(string dropMessages)
        {
            var script = string.Format(SqlScripts.ScriptDropAll, _dataBaseObjectsNamingConvention, dropMessages, _schemaName);
            return this.ActivateDatabaseLoging ? script : this.RemoveLogOperations(script);
        }

        protected virtual string RemoveLogOperations(string source)
        {
            while (true)
            {
                var startPos = source.IndexOf("PRINT N'SqlTableDependency:", StringComparison.InvariantCultureIgnoreCase);
                if (startPos < 1) break;

                var endPos = source.IndexOf(".';", startPos, StringComparison.InvariantCultureIgnoreCase);
                if (endPos < 1) break;

                source = source.Substring(0, startPos) + source.Substring(endPos + ".';".Length);
            }

            return source;
        }

        protected virtual string PrepareExceptStatement(IReadOnlyCollection<ColumnInfo> interestedColumns)
        {
            if (interestedColumns.Any(tableColumn =>
                string.Equals(tableColumn.Type.ToLowerInvariant(), "timestamp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tableColumn.Type.ToLowerInvariant(), "rowversion", StringComparison.OrdinalIgnoreCase))) return "INSERTED";

            var separatorNewColumns = new Separator(2, ",");
            var sBuilderNewColumns = new StringBuilder();
            var separatorOldColumns = new Separator(2, ",");
            var sBuilderOldColumns = new StringBuilder();

            foreach (var column in interestedColumns)
            {
                sBuilderNewColumns.Append($"{separatorNewColumns.GetSeparator()}[m_New].[{column.Name}]");
                sBuilderOldColumns.Append($"{separatorOldColumns.GetSeparator()}[m_Old].[{column.Name}]");
            }

            var exceptStatement = $"(SELECT {sBuilderNewColumns} FROM INSERTED AS [m_New] {this.CreateWhereCondifition()} EXCEPT SELECT {sBuilderOldColumns} FROM DELETED AS [m_Old]) a";

            return exceptStatement;
        }

        protected virtual IEnumerable<string> GetDmlTriggerType(DmlTriggerType dmlTriggerType)
        {
            var afters = new List<string>();
            if (dmlTriggerType.HasFlag(DmlTriggerType.All))
            {
                afters.Add(DmlTriggerType.Insert.ToString().ToLowerInvariant());
                afters.Add(DmlTriggerType.Update.ToString().ToLowerInvariant());
                afters.Add(DmlTriggerType.Delete.ToString().ToLowerInvariant());
            }
            else
            {
                if (dmlTriggerType.HasFlag(DmlTriggerType.Insert)) afters.Add(DmlTriggerType.Insert.ToString().ToLowerInvariant());
                if (dmlTriggerType.HasFlag(DmlTriggerType.Delete)) afters.Add(DmlTriggerType.Delete.ToString().ToLowerInvariant());
                if (dmlTriggerType.HasFlag(DmlTriggerType.Update)) afters.Add(DmlTriggerType.Update.ToString().ToLowerInvariant());
            }

            return afters;
        }

        protected virtual MessagesBag CreateMessagesBag(Encoding encoding, ICollection<string> processableMessages)
        {
            return new MessagesBag(
                encoding ?? Encoding.Unicode,
                new List<string> { string.Format(StartMessageTemplate, _dataBaseObjectsNamingConvention, ChangeType.Insert), string.Format(StartMessageTemplate, _dataBaseObjectsNamingConvention, ChangeType.Update), string.Format(StartMessageTemplate, _dataBaseObjectsNamingConvention, ChangeType.Delete) },
                string.Format(EndMessageTemplate, _dataBaseObjectsNamingConvention),
                processableMessages);
        }

        protected virtual string PrepareColumnListForSelectFromTableVariable(IEnumerable<ColumnInfo> tableColumns)
        {
            var columns = tableColumns.Select(c =>
            {
                var column = $"[{c.Name}]";

                if (this.IncludeOldValues)
                {
                    column += ", NULL";
                }

                return column;
            });

            return string.Join(", ", columns.ToList());
        }

        protected virtual string PrepareColumnListForTableVariable(IEnumerable<ColumnInfo> tableColumns)
        {
            var columns = tableColumns.Select(tableColumn =>
            {
                if (string.Equals(tableColumn.Type.ToLowerInvariant(), "timestamp", StringComparison.OrdinalIgnoreCase))
                {
                    var columnBinary = $"[{tableColumn.Name}] binary(8)";
                    if (this.IncludeOldValues) columnBinary += $", [{tableColumn.Name}_old] binary(8)";
                    return columnBinary;
                }

                if (string.Equals(tableColumn.Type.ToLowerInvariant(), "rowversion", StringComparison.OrdinalIgnoreCase))
                {
                    var columnVarbinary = $"[{tableColumn.Name}] varbinary(8)";
                    if (this.IncludeOldValues) columnVarbinary += $", [{ tableColumn.Name}_old] varbinary(8)";
                    return columnVarbinary;
                }

                if (!string.IsNullOrWhiteSpace(tableColumn.Size))
                {
                    var columnWithSize = $"[{tableColumn.Name}] {tableColumn.Type}({tableColumn.Size})";
                    if (this.IncludeOldValues) columnWithSize += $", [{tableColumn.Name}_old] {tableColumn.Type}({tableColumn.Size})";
                    return columnWithSize;
                }

                var column = $"[{tableColumn.Name}] {tableColumn.Type}";
                if (this.IncludeOldValues) column += $", [{tableColumn.Name}_old] {tableColumn.Type}";
                return column;
            });

            return string.Join(", ", columns.ToList());
        }

        protected virtual string ComputeSize(string dataType, string characterMaximumLength, string numericPrecision, string numericScale, string dateTimePrecisione)
        {
            if (string.Equals(dataType.ToUpperInvariant(), "BINARY", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dataType.ToUpperInvariant(), "VARBINARY", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dataType.ToUpperInvariant(), "CHAR", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dataType.ToUpperInvariant(), "NCHAR", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dataType.ToUpperInvariant(), "VARCHAR", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dataType.ToUpperInvariant(), "NVARCHAR", StringComparison.OrdinalIgnoreCase))
            {
                return characterMaximumLength == "-1" ? "MAX" : characterMaximumLength;
            }

            if (string.Equals(dataType.ToUpperInvariant(), "DECIMAL", StringComparison.OrdinalIgnoreCase))
            {
                return $"{numericPrecision},{numericScale}";
            }

            if (string.Equals(dataType.ToUpperInvariant(), "DATETIME2", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dataType.ToUpperInvariant(), "DATETIMEOFFSET", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dataType.ToUpperInvariant(), "TIME", StringComparison.OrdinalIgnoreCase))
            {
                return $"{dateTimePrecisione}";
            }

            return null;
        }

        protected override void CheckIfUserInterestedColumnsCanBeManaged()
        {
            var checkIfUserInterestedColumnsCanBeManaged = _userInterestedColumns as ColumnInfo[] ?? _userInterestedColumns.ToArray();
            foreach (var tableColumn in checkIfUserInterestedColumnsCanBeManaged)
            {
                if (string.Equals(tableColumn.Type.ToUpperInvariant(), "XML", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tableColumn.Type.ToUpperInvariant(), "IMAGE", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tableColumn.Type.ToUpperInvariant(), "TEXT", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tableColumn.Type.ToUpperInvariant(), "NTEXT", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tableColumn.Type.ToUpperInvariant(), "STRUCTURED", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tableColumn.Type.ToUpperInvariant(), "GEOGRAPHY", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tableColumn.Type.ToUpperInvariant(), "GEOMETRY", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tableColumn.Type.ToUpperInvariant(), "HIERARCHYID", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tableColumn.Type.ToUpperInvariant(), "SQL_VARIANT", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ColumnTypeNotSupportedException($"{tableColumn.Type} column type is not an supported by SqlTableDependency.");
                }
            }
        }

        protected virtual string ConvertFormat(ColumnInfo userInterestedColumn)
        {
            return string.Equals(userInterestedColumn.Type, "datetime", StringComparison.OrdinalIgnoreCase) || string.Equals(userInterestedColumn.Type, "date", StringComparison.OrdinalIgnoreCase) ? ", 121" : string.Empty;
        }

        protected virtual string ConvertValueByType(IReadOnlyCollection<ColumnInfo> userInterestedColumns, ColumnInfo userInterestedColumn, bool isOld = false)
        {
            var oldNameExtension = isOld ? "_old" : string.Empty;

            if (string.Equals(userInterestedColumn.Type, "binary", StringComparison.OrdinalIgnoreCase) || string.Equals(userInterestedColumn.Type, "varbinary", StringComparison.OrdinalIgnoreCase) || string.Equals(userInterestedColumn.Type, "timestamp", StringComparison.OrdinalIgnoreCase))
            {
                return this.SanitizeVariableName(userInterestedColumns, userInterestedColumn.Name) + oldNameExtension;
            }
            
            return $"CONVERT(NVARCHAR(MAX), {this.SanitizeVariableName(userInterestedColumns, userInterestedColumn.Name)}{oldNameExtension}{this.ConvertFormat(userInterestedColumn)})";
        }

        protected virtual string PrepareSendConversation(ChangeType dmlType, IReadOnlyCollection<ColumnInfo> userInterestedColumns)
        {
            var sendList = userInterestedColumns
                .Select(insterestedColumn =>
                {
                    var sendStatement = this.Spacer(16) + $"IF {this.SanitizeVariableName(userInterestedColumns, insterestedColumn.Name)} IS NOT NULL BEGIN" + Environment.NewLine + this.Spacer(20) + $";SEND ON CONVERSATION '{this.ConversationHandle}' MESSAGE TYPE [{_dataBaseObjectsNamingConvention}/{insterestedColumn.Name}] ({this.ConvertValueByType(userInterestedColumns, insterestedColumn)})" + Environment.NewLine + this.Spacer(16) + "END" + Environment.NewLine + this.Spacer(16) + "ELSE BEGIN" + Environment.NewLine + this.Spacer(20) + $";SEND ON CONVERSATION '{this.ConversationHandle}' MESSAGE TYPE [{_dataBaseObjectsNamingConvention}/{insterestedColumn.Name}] (0x)" + Environment.NewLine + this.Spacer(16) + "END";
                    if (this.IncludeOldValues)
                    {
                        sendStatement += Environment.NewLine + this.Spacer(16) + $"IF {this.SanitizeVariableName(userInterestedColumns, insterestedColumn.Name)}_old IS NOT NULL BEGIN" + Environment.NewLine + this.Spacer(20) + $";SEND ON CONVERSATION '{this.ConversationHandle}' MESSAGE TYPE [{_dataBaseObjectsNamingConvention}/{insterestedColumn.Name}/old] ({this.ConvertValueByType(userInterestedColumns, insterestedColumn, this.IncludeOldValues)})" + Environment.NewLine + this.Spacer(16) + "END" + Environment.NewLine + this.Spacer(16) + "ELSE BEGIN" + Environment.NewLine + this.Spacer(20) + $";SEND ON CONVERSATION '{this.ConversationHandle}' MESSAGE TYPE [{_dataBaseObjectsNamingConvention}/{insterestedColumn.Name}/old] (0x)" + Environment.NewLine + this.Spacer(16) + "END";
                    }

                    return sendStatement;
                })
                .ToList();

            sendList.Insert(0, $";SEND ON CONVERSATION '{this.ConversationHandle}' MESSAGE TYPE [{string.Format(StartMessageTemplate, _dataBaseObjectsNamingConvention, dmlType)}] (CONVERT(NVARCHAR, @dmlType))" + Environment.NewLine);
            sendList.Add(Environment.NewLine + this.Spacer(16) + $";SEND ON CONVERSATION '{this.ConversationHandle}' MESSAGE TYPE [{string.Format(EndMessageTemplate, _dataBaseObjectsNamingConvention)}] (0x)");

            return string.Join(Environment.NewLine, sendList);
        }

        protected virtual string PrepareSelectForSetVariables(IReadOnlyCollection<ColumnInfo> userInterestedColumns)
        {
            var result = string.Join(", ", userInterestedColumns.Select(insterestedColumn => $"{this.SanitizeVariableName(userInterestedColumns, insterestedColumn.Name)} = [{insterestedColumn.Name}]"));
            if (this.IncludeOldValues) result += ", " + string.Join(", ", userInterestedColumns.Select(insterestedColumn => $"{this.SanitizeVariableName(userInterestedColumns, insterestedColumn.Name)}_old = [{insterestedColumn.Name}_old]"));

            return result;
        }

        protected virtual string PrepareDeclareVariableStatement(IReadOnlyCollection<ColumnInfo> interestedColumns)
        {
            var colonne = (from insterestedColumn in interestedColumns
                           let variableType = $"{insterestedColumn.Type.ToLowerInvariant()}" + (string.IsNullOrWhiteSpace(insterestedColumn.Size)
                           ? string.Empty
                           : $"({insterestedColumn.Size})")
                           select this.DeclareStatement(interestedColumns, insterestedColumn, variableType)).ToList();

            return string.Join(Environment.NewLine + this.Spacer(4), colonne);
        }

        protected virtual string DeclareStatement(IReadOnlyCollection<ColumnInfo> interestedColumns, ColumnInfo insterestedColumn, string variableType)
        {
            var variableName = this.SanitizeVariableName(interestedColumns, insterestedColumn.Name);

            var declare = $"DECLARE {variableName} {variableType.ToLowerInvariant()}";
            if (this.IncludeOldValues) declare += $", {variableName}_old {variableType.ToLowerInvariant()}";

            return declare;
        }

        protected virtual string SanitizeVariableName(IReadOnlyCollection<ColumnInfo> userInterestedColumns, string tableColumnName)
        {
            for (var i = 0; i < userInterestedColumns.Count; i++)
            {
                if (userInterestedColumns.ElementAt(i).Name == tableColumnName)
                {
                    return "@var" + (i + 1);
                }
            }

            throw new SanitizeVariableNameException(tableColumnName);
        }

        protected override void CheckIfConnectionStringIsValid()
        {
            if (string.IsNullOrWhiteSpace(_connectionString)) throw new ArgumentNullException(nameof(_connectionString));

            SqlConnectionStringBuilder sqlConnectionStringBuilder;

            try
            {
                sqlConnectionStringBuilder = new SqlConnectionStringBuilder(_connectionString);
            }
            catch (Exception exception)
            {
                throw new InvalidConnectionStringException(_connectionString, exception);
            }

            using (var sqlConnection = new SqlConnection(sqlConnectionStringBuilder.ConnectionString))
            {
                try
                {
                    sqlConnection.Open();
                }
                catch (SqlException exception)
                {
                    throw new ImpossibleOpenSqlConnectionException(sqlConnectionStringBuilder.ConnectionString, exception);
                }
            }
        }

        protected override void CheckIfUserHasPermissions()
        {
            PrivilegesTable privilegesTable;

            using (var sqlConnection = new SqlConnection(_connectionString))
            {
                sqlConnection.Open();
                using (var sqlCommand = sqlConnection.CreateCommand())
                {
                    sqlCommand.CommandText = SqlScripts.SelectUserGrants;

                    var rows = SerializeSqlDataReader.Serialize(sqlCommand.ExecuteReader(CommandBehavior.CloseConnection));
                    privilegesTable = PrivilegesTable.FromEnumerable(rows);
                }
            }
            if (privilegesTable.Rows.Count == 0) throw new UserWithNoPermissionException();

            if (privilegesTable.Rows.Any(r => string.Equals(r.Role, "db_owner", StringComparison.OrdinalIgnoreCase)))
            {
                // Ok
            }
            else
            {
                foreach (var permission in Enum.GetValues(typeof(SqlServerRequiredPermission)))
                {
                    var permissionToCkeck = EnumUtil.GetDescriptionFromEnumValue((SqlServerRequiredPermission)permission);
                    if (privilegesTable.Rows.All(r => !string.Equals(r.PermissionType, permissionToCkeck, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new UserWithMissingPermissionException(permissionToCkeck);
                    }
                }
            }
        }

        protected virtual void CheckIfServiceBrokerIsEnabled()
        {
            using (var sqlConnection = new SqlConnection(_connectionString))
            {
                sqlConnection.Open();
                using (var sqlCommand = sqlConnection.CreateCommand())
                {
                    sqlCommand.CommandText = "SELECT is_broker_enabled FROM sys.databases WITH (NOLOCK) WHERE database_id = db_id();";
                    if ((bool)sqlCommand.ExecuteScalar() == false) throw new ServiceBrokerNotEnabledException();
                }
            }
        }

        protected override void CheckIfTableExists()
        {
            using (var sqlConnection = new SqlConnection(_connectionString))
            {
                sqlConnection.Open();
                using (var sqlCommand = sqlConnection.CreateCommand())
                {
                    sqlCommand.CommandText = string.Format(SqlScripts.InformationSchemaTables, _tableName, _schemaName);
                    if ((int)sqlCommand.ExecuteScalar() == 0) throw new NotExistingTableException(_tableName);
                }
            }
        }

        protected virtual async Task WaitForNotifications(
            CancellationToken cancellationToken,
            Delegate[] onChangeSubscribedList,
            Delegate[] onErrorSubscribedList,
            Delegate[] onStatusChangedSubscribedList,
            int timeOut,
            int timeOutWatchDog)
        {
            this.WriteTraceMessage(TraceLevel.Verbose, "Get in WaitForNotifications.");

            var messagesBag = this.CreateMessagesBag(this.Encoding, _processableMessages);

            var unqueueMessageNumber = _userInterestedColumns.Count() * (this.IncludeOldValues ? 2 : 1) + 2;

            var waitforSqlScript =
                $"BEGIN CONVERSATION TIMER ('{this.ConversationHandle.ToString().ToUpper()}') TIMEOUT = " + timeOutWatchDog + ";" +
                $"WAITFOR (RECEIVE TOP({unqueueMessageNumber}) [message_type_name], [message_body] FROM [{_schemaName}].[{_dataBaseObjectsNamingConvention}_Receiver]), TIMEOUT {timeOut * 1000};";

            this.NotifyListenersAboutStatus(onStatusChangedSubscribedList, TableDependencyStatus.Started);

            try
            {
                using (var sqlConnection = new SqlConnection(_connectionString))
                {
                    await sqlConnection.OpenAsync(cancellationToken);
                    this.WriteTraceMessage(TraceLevel.Verbose, "Connection opened.");
                    this.NotifyListenersAboutStatus(onStatusChangedSubscribedList, TableDependencyStatus.WaitingForNotification);

                    while (true)
                    {
                        messagesBag.Reset();

                        using (var sqlCommand = new SqlCommand(waitforSqlScript, sqlConnection))
                        {
                            sqlCommand.CommandTimeout = 0;
                            this.WriteTraceMessage(TraceLevel.Verbose, "Executing WAITFOR command.");

                            using (var sqlDataReader = await sqlCommand.ExecuteReaderAsync(cancellationToken).WithCancellation(cancellationToken))
                            {
                                while (sqlDataReader.Read())
                                {
                                    var message = new Message(sqlDataReader.GetSqlString(0).Value, sqlDataReader.IsDBNull(1) ? null : sqlDataReader.GetSqlBytes(1).Value);
                                    if (message.MessageType == SqlMessageTypes.ErrorType) throw new QueueContainingErrorMessageException();
                                    messagesBag.AddMessage(message);
                                    this.WriteTraceMessage(TraceLevel.Verbose, $"Received message type = {message.MessageType}.");
                                }
                            }
                        }

                        if (messagesBag.Status == MessagesBagStatus.Collecting)
                        {
                            throw new MessageMisalignedException("Received a number of message lower than expected.");
                        }

                        if (messagesBag.Status == MessagesBagStatus.Ready)
                        {
                            this.WriteTraceMessage(TraceLevel.Verbose, "Message ready to be notified.");
                            this.NotifyListenersAboutChange(onChangeSubscribedList, messagesBag);
                            this.WriteTraceMessage(TraceLevel.Verbose, "Message notified.");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                this.NotifyListenersAboutStatus(onStatusChangedSubscribedList, TableDependencyStatus.StopDueToCancellation);
                this.WriteTraceMessage(TraceLevel.Info, "Operation canceled.");
            }
            catch (AggregateException aggregateException)
            {
                this.NotifyListenersAboutStatus(onStatusChangedSubscribedList, TableDependencyStatus.StopDueToError);
                if (cancellationToken.IsCancellationRequested == false) this.NotifyListenersAboutError(onErrorSubscribedList, aggregateException.InnerException);
                this.WriteTraceMessage(TraceLevel.Error, "Exception in WaitForNotifications.", aggregateException.InnerException);
            }
            catch (SqlException sqlException)
            {
                this.NotifyListenersAboutStatus(onStatusChangedSubscribedList, TableDependencyStatus.StopDueToError);
                if (cancellationToken.IsCancellationRequested == false) this.NotifyListenersAboutError(onErrorSubscribedList, sqlException);
                this.WriteTraceMessage(TraceLevel.Error, "Exception in WaitForNotifications.", sqlException);
            }
            catch (Exception exception)
            {
                this.NotifyListenersAboutStatus(onStatusChangedSubscribedList, TableDependencyStatus.StopDueToError);
                if (cancellationToken.IsCancellationRequested == false) this.NotifyListenersAboutError(onErrorSubscribedList, exception);
                this.WriteTraceMessage(TraceLevel.Error, "Exception in WaitForNotifications.", exception);
            }
        }
    }

    #endregion
}