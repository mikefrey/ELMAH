#region License, Terms and Author(s)
//
// ELMAH - Error Logging Modules and Handlers for ASP.NET
// Copyright (c) 2004-9 Atif Aziz. All rights reserved.
//
//  Author(s):
//
//      James Driscoll, mailto:jamesdriscoll@btinternet.com
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//    http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
#endregion

// All code in this file requires .NET Framework 2.0 or later.

#if !NET_1_1 && !NET_1_0

[assembly: Elmah.Scc("$Id: VistaDBErrorLog.cs 566 2009-05-11 10:37:10Z azizatif $")]

namespace Elmah
{
    #region Imports

    using System;
    using System.Data;
    using System.Globalization;
    using System.IO;
    using System.Text.RegularExpressions;
    using VistaDB;
    using VistaDB.DDA;
    using VistaDB.Provider;

    using IDictionary = System.Collections.IDictionary;
    using IList = System.Collections.IList;

    #endregion

    /// <summary>
    /// An <see cref="ErrorLog"/> implementation that uses VistaDB as its backing store.
    /// </summary>

    public class VistaDBErrorLog : ErrorLog
    {
        private readonly string _connectionString;
        private readonly string _databasePath;

        // TODO - don't think we have to limit strings in VistaDB, so decide if we really need this
        // Is it better to keep it for consistency, or better to exploit the full potential of the database??
        private const int _maxAppNameLength = 60;

        /// <summary>
        /// Initializes a new instance of the <see cref="VistaDBErrorLog"/> class
        /// using a dictionary of configured settings.
        /// </summary>

        public VistaDBErrorLog(IDictionary config)
        {
            if (config == null)
                throw new ArgumentNullException("config");

            _connectionString = ConnectionStringHelper.GetConnectionString(config);

            //
            // If there is no connection string to use then throw an 
            // exception to abort construction.
            //

            if (_connectionString.Length == 0)
                throw new ApplicationException("Connection string is missing for the VistaDB error log.");

            _databasePath = ConnectionStringHelper.GetDataSourceFilePath(_connectionString);
            InitializeDatabase();

            string appName = Mask.NullString((string)config["applicationName"]);

            if (appName.Length > _maxAppNameLength)
            {
                throw new ApplicationException(string.Format(
                    "Application name is too long. Maximum length allowed is {0} characters.",
                    _maxAppNameLength.ToString("N0")));
            }

            ApplicationName = appName;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="VistaDBErrorLog"/> class
        /// to use a specific connection string for connecting to the database.
        /// </summary>

        public VistaDBErrorLog(string connectionString)
        {
            if (connectionString == null)
                throw new ArgumentNullException("connectionString");

            if (connectionString.Length == 0)
                throw new ArgumentException(null, "connectionString");

            _connectionString = connectionString;
            _databasePath = ConnectionStringHelper.GetDataSourceFilePath(_connectionString);

            InitializeDatabase();
        }

        /// <summary>
        /// Gets the name of this error log implementation.
        /// </summary>

        public override string Name
        {
            get { return "VistaDB Error Log"; }
        }

        /// <summary>
        /// Gets the connection string used by the log to connect to the database.
        /// </summary>

        public virtual string ConnectionString
        {
            get { return _connectionString; }
        }

        /// <summary>
        /// Logs an error to the database.
        /// </summary>
        /// <remarks>
        /// Use the stored procedure called by this implementation to set a
        /// policy on how long errors are kept in the log. The default
        /// implementation stores all errors for an indefinite time.
        /// </remarks>

        public override string Log(Error error)
        {
            if (error == null)
                throw new ArgumentNullException("error");

            string errorXml = ErrorXml.EncodeString(error);

            using (VistaDBConnection connection = new VistaDBConnection(this.ConnectionString))
            using (VistaDBCommand command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = @"INSERT INTO ELMAH_Error
                                            (Application, Host, Type, Source, 
                                            Message, [User], AllXml, StatusCode, TimeUtc)
                                        VALUES
                                            (@Application, @Host, @Type, @Source,
                                            @Message, @User, @AllXml, @StatusCode, @TimeUtc);

                                        SELECT @@IDENTITY";
                command.CommandType = CommandType.Text;

                VistaDBParameterCollection parameters = command.Parameters;
                parameters.Add("@Application", VistaDBType.NVarChar, _maxAppNameLength).Value = ApplicationName;
                parameters.Add("@Host", VistaDBType.NVarChar, 30).Value = error.HostName;
                parameters.Add("@Type", VistaDBType.NVarChar, 100).Value = error.Type;
                parameters.Add("@Source", VistaDBType.NVarChar, 60).Value = error.Source;
                parameters.Add("@Message", VistaDBType.NVarChar, 500).Value = error.Message;
                parameters.Add("@User", VistaDBType.NVarChar, 50).Value = error.User;
                parameters.Add("@AllXml", VistaDBType.NText).Value = errorXml;
                parameters.Add("@StatusCode", VistaDBType.Int).Value = error.StatusCode;
                parameters.Add("@TimeUtc", VistaDBType.DateTime).Value = error.Time.ToUniversalTime();

                return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Returns a page of errors from the databse in descending order 
        /// of logged time.
        /// </summary>

        public override int GetErrors(int pageIndex, int pageSize, IList errorEntryList)
        {
            if (pageIndex < 0)
                throw new ArgumentOutOfRangeException("pageIndex", pageIndex, null);

            if (pageSize < 0)
                throw new ArgumentOutOfRangeException("pageSize", pageSize, null);

            VistaDBConnectionStringBuilder builder = new VistaDBConnectionStringBuilder(_connectionString);


            // Use the VistaDB Direct Data Access objects
            IVistaDBDDA ddaObjects = VistaDBEngine.Connections.OpenDDA();
            // Create a connection object to a VistaDB database
            IVistaDBDatabase vistaDB = ddaObjects.OpenDatabase(_databasePath, builder.OpenMode, builder.Password);
            // Open the table
            IVistaDBTable elmahTable = vistaDB.OpenTable("ELMAH_Error", false, true);

            elmahTable.ActiveIndex = "IX_ELMAH_Error_App_Time_Id";

            if (errorEntryList != null)
            {
                if (!elmahTable.EndOfTable)
                {
                    // move to the correct record
                    elmahTable.First();
                    elmahTable.MoveBy(pageIndex * pageSize);

                    int rowsProcessed = 0;

                    // Traverse the table to get the records we want
                    while (!elmahTable.EndOfTable && rowsProcessed < pageSize)
                    {
                        rowsProcessed++;

                        string id = Convert.ToString(elmahTable.Get("ErrorId").Value, CultureInfo.InvariantCulture);
                        Error error = new Error();

                        error.ApplicationName = (string)elmahTable.Get("Application").Value;
                        error.HostName = (string)elmahTable.Get("Host").Value;
                        error.Type = (string)elmahTable.Get("Type").Value;
                        error.Source = (string)elmahTable.Get("Source").Value;
                        error.Message = (string)elmahTable.Get("Message").Value;
                        error.User = (string)elmahTable.Get("User").Value;
                        error.StatusCode = (int)elmahTable.Get("StatusCode").Value;
                        error.Time = ((DateTime)elmahTable.Get("TimeUtc").Value).ToLocalTime();

                        errorEntryList.Add(new ErrorLogEntry(this, id, error));

                        // move to the next record
                        elmahTable.Next();
                    }
                }
            }
            
            return Convert.ToInt32(elmahTable.RowCount);
        }

        /// <summary>
        /// Returns the specified error from the database, or null 
        /// if it does not exist.
        /// </summary>

        public override ErrorLogEntry GetError(string id)
        {
            if (id == null)
                throw new ArgumentNullException("id");

            if (id.Length == 0)
                throw new ArgumentException(null, "id");

            int errorId;
            try
            {
                errorId = int.Parse(id, CultureInfo.InvariantCulture);
            }
            catch (FormatException e)
            {
                throw new ArgumentException(e.Message, "id", e);
            }
            catch (OverflowException e)
            {
                throw new ArgumentException(e.Message, "id", e);
            }

            string errorXml;

            using (VistaDBConnection connection = new VistaDBConnection(this.ConnectionString))
            using (VistaDBCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT  AllXml
                                        FROM    ELMAH_Error
                                        WHERE   ErrorId = @ErrorId";
                command.CommandType = CommandType.Text;

                VistaDBParameterCollection parameters = command.Parameters;
                parameters.Add("@ErrorId", VistaDBType.Int).Value = errorId;

                connection.Open();
                
                // NB this has been deliberately done like this as command.ExecuteScalar 
                // is not exhibiting the expected behaviour in VistaDB at the moment
                using (VistaDBDataReader dr = command.ExecuteReader())
                {
                    if (dr.Read())
                        errorXml = dr[0] as string;
                    else
                        errorXml = null;
                }
            }

            if (errorXml == null)
                return null;

            Error error = ErrorXml.DecodeString(errorXml);
            return new ErrorLogEntry(this, id, error);
        }

        private static string EscapeApostrophes(string text)
        {
            return text.Replace("'", "''");
        }

        private static readonly object _lock = new object();
        private void InitializeDatabase()
        {
            string connectionString = ConnectionString;
            Debug.AssertStringNotEmpty(connectionString);

            if (File.Exists(_databasePath))
                return;

            //
            // Make sure that we don't have multiple threads all trying to create the database
            //

            lock (_lock)
            {
                //
                // Just double check that no other thread has created the database while
                // we were waiting for the lock
                //

                if (File.Exists(_databasePath))
                    return;

                VistaDBConnectionStringBuilder builder = new VistaDBConnectionStringBuilder(connectionString);

                using (VistaDBConnection connection = new VistaDBConnection())
                using (VistaDBCommand command = connection.CreateCommand())
                {
                    string passwordClause = string.Empty;
                    if (!string.IsNullOrEmpty(builder.Password))
                        passwordClause = " PASSWORD '" + EscapeApostrophes(builder.Password) + "',";

                    // create the database using the webserver's default locale
                    command.CommandText = "CREATE DATABASE '" + EscapeApostrophes(_databasePath) + "'" + passwordClause + ", PAGE SIZE 1, CASE SENSITIVE FALSE;";
                    command.ExecuteNonQuery();

                    const string ddlScript = @"
                    CREATE TABLE [ELMAH_Error]
                    (
                        [ErrorId] INT NOT NULL,
                        [Application] NVARCHAR (60) NOT NULL,
                        [Host] NVARCHAR (50) NOT NULL,
                        [Type] NVARCHAR (100) NOT NULL,
                        [Source] NVARCHAR (60) NOT NULL,
                        [Message] NVARCHAR (500) NOT NULL,
                        [User] NVARCHAR (50) NOT NULL,
                        [StatusCode] INT NOT NULL,
                        [TimeUtc] DATETIME NOT NULL,
                        [AllXml] NTEXT NOT NULL,
                        CONSTRAINT [PK_ELMAH_Error] PRIMARY KEY ([ErrorId])
                    )

                    GO

                    ALTER TABLE [ELMAH_Error]
                    ALTER COLUMN [ErrorId] INT NOT NULL IDENTITY (1, 1)

                    GO

                    CREATE INDEX [IX_ELMAH_Error_App_Time_Id] ON [ELMAH_Error] ([TimeUtc] DESC, [ErrorId] DESC)";

                    foreach (string batch in ScriptToBatches(ddlScript))
                    {
                        command.CommandText = batch;
                        command.ExecuteNonQuery();
                    }
                }
            }
        }

        private static string[] ScriptToBatches(string script)
        {
            return Regex.Split(script, @"^ \s* GO \s* $\n?",
                        RegexOptions.IgnoreCase
                        | RegexOptions.Multiline
                        | RegexOptions.CultureInvariant
                        | RegexOptions.IgnorePatternWhitespace);
        }
    }
}

#endif //!NET_1_1 && !NET_1_0
