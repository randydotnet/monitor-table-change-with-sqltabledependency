﻿using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using TableDependency.Enums;
using TableDependency.EventArgs;
using TableDependency.SqlClient.BaseTests;

namespace TableDependency.SqlClient.IntegrationTests
{
    [Table("XXXX")]
    public class DataAnnotationTestSqlServer4Model
    {
        public long Id { get; set; }

        public string Name { get; set; }

        [Column("YYYY")]
        public string Description { get; set; }
    }

    [TestClass]
    public class DataAnnotationTest04 : SqlTableDependencyBaseTest
    {
        private const string TableName = "ANItemsTableSQL4";
        private static int _counter;
        private static Dictionary<string, Tuple<DataAnnotationTestSqlServer4Model, DataAnnotationTestSqlServer4Model>> _checkValues = new Dictionary<string, Tuple<DataAnnotationTestSqlServer4Model, DataAnnotationTestSqlServer4Model>>();
        private static Dictionary<string, Tuple<DataAnnotationTestSqlServer4Model, DataAnnotationTestSqlServer4Model>> _checkValuesOld = new Dictionary<string, Tuple<DataAnnotationTestSqlServer4Model, DataAnnotationTestSqlServer4Model>>();

        [ClassInitialize]
        public static void ClassInitialize(TestContext testContext)
        {
            using (var sqlConnection = new SqlConnection(ConnectionStringForTestUser))
            {
                sqlConnection.Open();
                using (var sqlCommand = sqlConnection.CreateCommand())
                {
                    sqlCommand.CommandText = $"IF OBJECT_ID('{TableName}', 'U') IS NOT NULL DROP TABLE [{TableName}];";
                    sqlCommand.ExecuteNonQuery();

                    sqlCommand.CommandText = $"CREATE TABLE [{TableName}]([Id] [int] IDENTITY(1, 1) NOT NULL, [Name] [NVARCHAR](50) NULL, [Long Description] [NVARCHAR](MAX) NULL)";
                    sqlCommand.ExecuteNonQuery();
                }
            }
        }

        [TestInitialize]
        public void TestInitialize()
        {
            using (var sqlConnection = new SqlConnection(ConnectionStringForTestUser))
            {
                sqlConnection.Open();
                using (var sqlCommand = sqlConnection.CreateCommand())
                {
                    sqlCommand.CommandText = $"DELETE FROM [{TableName}];";
                    sqlCommand.ExecuteNonQuery();
                }
            }

            _checkValues.Clear();
            _checkValuesOld.Clear();

            _counter = 0;

            _checkValues.Add(ChangeType.Insert.ToString(), new Tuple<DataAnnotationTestSqlServer4Model, DataAnnotationTestSqlServer4Model>(new DataAnnotationTestSqlServer4Model { Name = "Christian", Description = "Del Bianco" }, new DataAnnotationTestSqlServer4Model()));
            _checkValues.Add(ChangeType.Update.ToString(), new Tuple<DataAnnotationTestSqlServer4Model, DataAnnotationTestSqlServer4Model>(new DataAnnotationTestSqlServer4Model { Name = "Velia", Description = "Ceccarelli" }, new DataAnnotationTestSqlServer4Model()));
            _checkValues.Add(ChangeType.Delete.ToString(), new Tuple<DataAnnotationTestSqlServer4Model, DataAnnotationTestSqlServer4Model>(new DataAnnotationTestSqlServer4Model { Name = "Velia", Description = "Ceccarelli" }, new DataAnnotationTestSqlServer4Model()));

            _checkValuesOld.Add(ChangeType.Insert.ToString(), new Tuple<DataAnnotationTestSqlServer4Model, DataAnnotationTestSqlServer4Model>(new DataAnnotationTestSqlServer4Model { Name = "Christian", Description = "Del Bianco" }, new DataAnnotationTestSqlServer4Model()));
            _checkValuesOld.Add(ChangeType.Update.ToString(), new Tuple<DataAnnotationTestSqlServer4Model, DataAnnotationTestSqlServer4Model>(new DataAnnotationTestSqlServer4Model { Name = "Velia", Description = "Ceccarelli" }, new DataAnnotationTestSqlServer4Model()));
            _checkValuesOld.Add(ChangeType.Delete.ToString(), new Tuple<DataAnnotationTestSqlServer4Model, DataAnnotationTestSqlServer4Model>(new DataAnnotationTestSqlServer4Model { Name = "Velia", Description = "Ceccarelli" }, new DataAnnotationTestSqlServer4Model()));            
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            using (var sqlConnection = new SqlConnection(ConnectionStringForTestUser))
            {
                sqlConnection.Open();
                using (var sqlCommand = sqlConnection.CreateCommand())
                {
                    sqlCommand.CommandText = $"IF OBJECT_ID('{TableName}', 'U') IS NOT NULL DROP TABLE [{TableName}];";
                    sqlCommand.ExecuteNonQuery();
                }
            }
        }

        [TestCategory("SqlServer")]
        [TestMethod]
        public void Test()
        {
            SqlTableDependency<DataAnnotationTestSqlServer4Model> tableDependency = null;
            string naming;

            var mapper = new ModelToTableMapper<DataAnnotationTestSqlServer4Model>();
            mapper.AddMapping(c => c.Description, "Long Description");

            try
            {
                tableDependency = new SqlTableDependency<DataAnnotationTestSqlServer4Model>(ConnectionStringForTestUser, tableName: TableName, mapper: mapper);
                tableDependency.OnChanged += TableDependency_Changed;
                tableDependency.Start();
                naming = tableDependency.DataBaseObjectsNamingConvention;

                var t = new Task(ModifyTableContent);
                t.Start();
                Thread.Sleep(1000 * 15 * 1);
            }
            finally
            {
                tableDependency?.Dispose();
            }

            Assert.AreEqual(_counter, 3);

            Assert.AreEqual(_checkValues[ChangeType.Insert.ToString()].Item2.Name, _checkValues[ChangeType.Insert.ToString()].Item1.Name);
            Assert.AreEqual(_checkValues[ChangeType.Insert.ToString()].Item2.Description, _checkValues[ChangeType.Insert.ToString()].Item1.Description);
            Assert.IsNull(_checkValuesOld[ChangeType.Insert.ToString()]);

            Assert.AreEqual(_checkValues[ChangeType.Update.ToString()].Item2.Name, _checkValues[ChangeType.Update.ToString()].Item1.Name);
            Assert.AreEqual(_checkValues[ChangeType.Update.ToString()].Item2.Description, _checkValues[ChangeType.Update.ToString()].Item1.Description);
            Assert.IsNull(_checkValuesOld[ChangeType.Update.ToString()]);

            Assert.AreEqual(_checkValues[ChangeType.Delete.ToString()].Item2.Name, _checkValues[ChangeType.Delete.ToString()].Item1.Name);
            Assert.AreEqual(_checkValues[ChangeType.Delete.ToString()].Item2.Description, _checkValues[ChangeType.Delete.ToString()].Item1.Description);
            Assert.IsNull(_checkValuesOld[ChangeType.Delete.ToString()]);

            Assert.IsTrue(base.AreAllDbObjectDisposed(naming));
            Assert.IsTrue(base.CountConversationEndpoints(naming) == 0);
        }

        [TestCategory("SqlServer")]
        [TestMethod]
        public void TestWithOldValues()
        {
            SqlTableDependency<DataAnnotationTestSqlServer4Model> tableDependency = null;
            string naming;

            var mapper = new ModelToTableMapper<DataAnnotationTestSqlServer4Model>();
            mapper.AddMapping(c => c.Description, "Long Description");

            try
            {
                tableDependency = new SqlTableDependency<DataAnnotationTestSqlServer4Model>(ConnectionStringForTestUser, includeOldValues: true, tableName: TableName, mapper: mapper);
                tableDependency.OnChanged += TableDependency_Changed;
                tableDependency.Start();
                naming = tableDependency.DataBaseObjectsNamingConvention;

                var t = new Task(ModifyTableContent);
                t.Start();
                Thread.Sleep(1000 * 15 * 1);
            }
            finally
            {
                tableDependency?.Dispose();
            }

            Assert.AreEqual(_counter, 3);

            Assert.AreEqual(_checkValues[ChangeType.Insert.ToString()].Item2.Name, _checkValues[ChangeType.Insert.ToString()].Item1.Name);
            Assert.AreEqual(_checkValues[ChangeType.Insert.ToString()].Item2.Description, _checkValues[ChangeType.Insert.ToString()].Item1.Description);
            Assert.IsNull(_checkValuesOld[ChangeType.Insert.ToString()]);

            Assert.AreEqual(_checkValues[ChangeType.Update.ToString()].Item2.Name, _checkValues[ChangeType.Update.ToString()].Item1.Name);
            Assert.AreEqual(_checkValues[ChangeType.Update.ToString()].Item2.Description, _checkValues[ChangeType.Update.ToString()].Item1.Description);
            Assert.AreEqual(_checkValuesOld[ChangeType.Update.ToString()].Item2.Name, _checkValues[ChangeType.Insert.ToString()].Item2.Name);
            Assert.AreEqual(_checkValuesOld[ChangeType.Update.ToString()].Item2.Description, _checkValues[ChangeType.Insert.ToString()].Item2.Description);

            Assert.AreEqual(_checkValues[ChangeType.Delete.ToString()].Item2.Name, _checkValues[ChangeType.Delete.ToString()].Item1.Name);
            Assert.AreEqual(_checkValues[ChangeType.Delete.ToString()].Item2.Description, _checkValues[ChangeType.Delete.ToString()].Item1.Description);
            Assert.IsNull(_checkValuesOld[ChangeType.Delete.ToString()]);

            Assert.IsTrue(base.AreAllDbObjectDisposed(naming));
            Assert.IsTrue(base.CountConversationEndpoints(naming) == 0);
        }

        private static void TableDependency_Changed(object sender, RecordChangedEventArgs<DataAnnotationTestSqlServer4Model> e)
        {
            _counter++;

            switch (e.ChangeType)
            {
                case ChangeType.Insert:
                    _checkValues[ChangeType.Insert.ToString()].Item2.Name = e.Entity.Name;
                    _checkValues[ChangeType.Insert.ToString()].Item2.Description = e.Entity.Description;

                    if (e.EntityOldValues != null)
                    {
                        _checkValuesOld[ChangeType.Insert.ToString()].Item2.Name = e.EntityOldValues.Name;
                        _checkValuesOld[ChangeType.Insert.ToString()].Item2.Description = e.EntityOldValues.Description;
                    }
                    else
                    {
                        _checkValuesOld[ChangeType.Insert.ToString()] = null;
                    }

                    break;

                case ChangeType.Update:
                    _checkValues[ChangeType.Update.ToString()].Item2.Name = e.Entity.Name;
                    _checkValues[ChangeType.Update.ToString()].Item2.Description = e.Entity.Description;

                    if (e.EntityOldValues != null)
                    {
                        _checkValuesOld[ChangeType.Update.ToString()].Item2.Name = e.EntityOldValues.Name;
                        _checkValuesOld[ChangeType.Update.ToString()].Item2.Description = e.EntityOldValues.Description;
                    }
                    else
                    {
                        _checkValuesOld[ChangeType.Update.ToString()] = null;
                    }

                    break;

                case ChangeType.Delete:
                    _checkValues[ChangeType.Delete.ToString()].Item2.Name = e.Entity.Name;
                    _checkValues[ChangeType.Delete.ToString()].Item2.Description = e.Entity.Description;

                    if (e.EntityOldValues != null)
                    {
                        _checkValuesOld[ChangeType.Delete.ToString()].Item2.Name = e.EntityOldValues.Name;
                        _checkValuesOld[ChangeType.Delete.ToString()].Item2.Description = e.EntityOldValues.Description;
                    }
                    else
                    {
                        _checkValuesOld[ChangeType.Delete.ToString()] = null;
                    }

                    break;
            }
        }

        private static void ModifyTableContent()
        {
            using (var sqlConnection = new SqlConnection(ConnectionStringForTestUser))
            {
                sqlConnection.Open();

                using (var sqlCommand = sqlConnection.CreateCommand())
                {
                    sqlCommand.CommandText = $"INSERT INTO [{TableName}] ([Name], [Long Description]) VALUES ('{_checkValues[ChangeType.Insert.ToString()].Item1.Name}', '{_checkValues[ChangeType.Insert.ToString()].Item1.Description}')";
                    sqlCommand.ExecuteNonQuery();
                }

                using (var sqlCommand = sqlConnection.CreateCommand())
                {
                    sqlCommand.CommandText = $"UPDATE [{TableName}] SET [Name] = '{_checkValues[ChangeType.Update.ToString()].Item1.Name}', [Long Description] = '{_checkValues[ChangeType.Update.ToString()].Item1.Description}'";
                    sqlCommand.ExecuteNonQuery();
                }

                using (var sqlCommand = sqlConnection.CreateCommand())
                {
                    sqlCommand.CommandText = $"DELETE FROM [{TableName}]";
                    sqlCommand.ExecuteNonQuery();
                }
            }
        }
    }
}