﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace NextCloudFileUploader
{
	public class FileService
	{
		private WebDavProvider _webDavProvider;
		private IDbConnection _dbConnection;

		public FileService(WebDavProvider webDavProvider, IDbConnection dbConnection)
		{
			_webDavProvider = webDavProvider;
			_dbConnection = dbConnection;
		}

        /// <summary>
        /// Выгружает файлы в хранилище.
        /// </summary>
        /// <param name="fileList">Список файлов, которые следует выгрузить</param>
        /// <param name="clearTempTableAfter">Флаг, указывающий на необходимость очисти временной таблицы после удачной выгрузки</param>
        /// /// <param name="saveDataAboutUploadedFiles">Флаг, указывающий на необходимость сохранить данные об успешно выгруженных файлах во временную таблицу</param>
        public async Task<bool> UploadFiles(IEnumerable<File> fileList, bool clearTempTableAfter, bool saveDataAboutUploadedFiles)
		{
			var files = fileList.ToList();
			var current = 0;

			foreach (var file in files)
			{
				try
				{
					var result = await _webDavProvider.PutWithHttp(file, current, files.Count);
				}
				catch (Exception ex)
				{
					SaveLoadedFilesToTempTable(file, files);
					ExceptionHandler.LogExceptionToConsole(ex);
					throw ex;
				}
				finally
				{
					Interlocked.Increment(ref current);
				}
			}

			if (clearTempTableAfter && !saveDataAboutUploadedFiles)
				DeleteFilesFromTempTable();
            if (saveDataAboutUploadedFiles && !clearTempTableAfter)
                SaveAllLoadedFilesToTempTable(files);
			if (clearTempTableAfter && saveDataAboutUploadedFiles)
			{
				DeleteFilesFromTempTable();
				SaveAllLoadedFilesToTempTable(files);
			}

			return true;
		}

		/// <summary>
		/// Выбирает из базы все файлы по имени сущности за исключением файлов, загруженных при предыдущем запуске.
		/// </summary>
		/// <param name="entity">Название сущности</param>
		/// <returns>Возвращает файлы для выгрузки в хранилище</returns>
		public IEnumerable<File> GetFilesFromDb(string entity)
		{
			try
			{
				if ((_dbConnection.State & ConnectionState.Open) == 0)
					_dbConnection.Open();

				var cmdSqlCommand = "";
				if (entity.Equals("Account") || entity.Equals("Contact"))
					cmdSqlCommand =
						$@"SELECT {Program.Top} '{entity}File' as Entity, f.{entity}Id as 'EntityId', f.Id as 'FileId', f.Version, f.Data FROM [dbo].[{entity}File] f 
						WITH (NOLOCK) 
						WHERE f.{entity}Id is not null AND f.Id is not null AND f.Data is not null AND
						(f.{entity}Id not in (SELECT EntityId FROM [dbo].[LastFileUploadedToNextCloud] WHERE EntityId = f.{entity}Id AND FileId = f.Id AND Version = f.Version))
						ORDER BY f.CreatedOn";
				if (entity.Equals("Contract"))
					cmdSqlCommand =
						$@"SELECT {Program.Top} '{entity}File' as Entity, f.{entity}Id as 'EntityId', f.Id as 'FileId', fv.PTVersion as 'Version', fv.PTData as 'Data' FROM [dbo].[{entity}File] f, [dbo].[PTFileVersion] fv 
						WITH (NOLOCK) 
						WHERE fv.PTFile = f.Id AND f.{entity}Id is not null AND f.Id is not null AND f.Data is not null AND 
						(f.{entity}Id not in (SELECT EntityId FROM [dbo].[LastFileUploadedToNextCloud] WHERE EntityId = f.{entity}Id AND FileId = f.Id AND Version = f.Version))
						ORDER BY f.CreatedOn";

				return _dbConnection.Query<File>(cmdSqlCommand).ToList();
			}
			catch (Exception ex)
			{
				ExceptionHandler.LogExceptionToConsole(ex);
				throw ex;
			}
			finally
			{
				if ((_dbConnection.State & ConnectionState.Open) != 0) _dbConnection.Close();
			}
		}

		/// <summary>
		/// Сохраняет данные файлов, которые были успешно выгружены в хранилище до ошибки.
		/// </summary>
		/// <param name="file">Последний выгруженный файл</param>
		/// <param name="fileList">Список файлов</param>
		private void SaveLoadedFilesToTempTable(File file, List<File> fileList)
		{
			try
			{
				if ((_dbConnection.State & ConnectionState.Open) == 0)
					_dbConnection.Open();

				var counter = 0;
				var values = " VALUES";
				var loadedFiles = new List<File>();
				var lastFileIndex = fileList.FindIndex(f => f.Entity == file.Entity
											 && f.EntityId == file.EntityId
											 && f.FileId == file.FileId
											 && f.Version == file.Version);
				if (lastFileIndex > 0)
					loadedFiles = fileList.GetRange(0, lastFileIndex);

				var dbCommand = _dbConnection.CreateCommand();

				foreach (var f in loadedFiles)
				{
					if (string.IsNullOrEmpty(f.Entity) || string.IsNullOrEmpty(f.EntityId) ||
					    string.IsNullOrEmpty(f.FileId) || string.IsNullOrEmpty(f.Version)) continue;

					values += $" ('{f.Entity}', '{f.EntityId}', '{f.FileId}', '{f.Version}', @Data{counter}),";
					dbCommand.Parameters.Add(new SqlParameter($"@Data{counter}", SqlDbType.VarBinary) { Value = f.Data });
				}

				var valuesWithoutLastComma = values.Remove(values.Length - 1, 1);

				dbCommand.CommandText = $@"BEGIN TRAN
												 BEGIN
													INSERT INTO [dbo].[LastFileUploadedToNextCloud] (Entity, EntityId, FileId, Version, Data)
													{valuesWithoutLastComma}
												 END
												 COMMIT TRAN";

				var result = dbCommand.ExecuteNonQuery();
				if (result != loadedFiles.Count)
					throw new Exception("Сохранились не все выгруженные файлы");
			}
			catch (Exception ex)
			{
				ExceptionHandler.LogExceptionToConsole(ex);
				throw ex;
			}
			finally
			{
				if ((_dbConnection.State & ConnectionState.Open) != 0) _dbConnection.Close();
			}
		}

        /// <summary>
		/// Сохраняет данные всех файлов, которые были успешно выгружены в хранилище.
		/// </summary>
		/// <param name="fileList">Список всех файлов</param>
		private void SaveAllLoadedFilesToTempTable(List<File> fileList)
        {
            try
            {
                if ((_dbConnection.State & ConnectionState.Open) == 0)
                    _dbConnection.Open();

	            var counter = 0;
                var values = " VALUES";

				var dbCommand = _dbConnection.CreateCommand();

				foreach (var f in fileList)
                {
	                if (string.IsNullOrEmpty(f.Entity) || string.IsNullOrEmpty(f.EntityId) ||
	                    string.IsNullOrEmpty(f.FileId) || string.IsNullOrEmpty(f.Version)) continue;

	                values += $" ('{f.Entity}', '{f.EntityId}', '{f.FileId}', '{f.Version}', @Data{counter}),";
	                dbCommand.Parameters.Add(new SqlParameter($"@Data{counter}", SqlDbType.VarBinary) { Value = f.Data });
	                counter++;
                }

                var valuesWithoutLastComma = values.Remove(values.Length - 1, 1);

                dbCommand.CommandText = $@"BEGIN TRAN
												 BEGIN
													INSERT INTO [dbo].[LastFileUploadedToNextCloud] (Entity, EntityId, FileId, Version, Data)
													{valuesWithoutLastComma}
												 END
										   COMMIT TRAN";

                var result = dbCommand.ExecuteNonQuery();
                if (result != fileList.Count)
                    throw new Exception("Сохранились не все выгруженные файлы");
            }
            catch (Exception ex)
            {
                ExceptionHandler.LogExceptionToConsole(ex);
                throw ex;
            }
            finally
            {
                if ((_dbConnection.State & ConnectionState.Open) != 0) _dbConnection.Close();
            }
        }

        /// <summary>
        /// Проверяет наличие записей в таблице файлов, которые были успешно выгружены в хранилище до ошибки.
        /// </summary>
        /// <returns>Возвращает флаг, пуста ли таблица записей файлов, которые были успешно выгружены в хранилище до ошибки</returns>
        public bool CheckTempTableEntriesCount()
		{
			try
			{
				if ((_dbConnection.State & ConnectionState.Open) == 0)
					_dbConnection.Open();

				var dbCommand = _dbConnection.CreateCommand();
				dbCommand.CommandText = @"SELECT COUNT(*) FROM [dbo].[LastFileUploadedToNextCloud] WITH (NOLOCK)";
				var result = dbCommand.ExecuteScalar();
				return int.Parse(result.ToString()) > 0;
			}
			catch (Exception ex)
			{
				ExceptionHandler.LogExceptionToConsole(ex);
				throw ex;
			}
			finally
			{
				if ((_dbConnection.State & ConnectionState.Open) != 0) _dbConnection.Close();
			}
		}

		/// <summary>
		/// Очищает таблицу записей удачно выгруженных файлов.
		/// </summary>
		private void DeleteFilesFromTempTable()
		{
			try
			{
				if ((_dbConnection.State & ConnectionState.Open) == 0)
					_dbConnection.Open();

				var dbCommand = _dbConnection.CreateCommand();
				dbCommand.CommandText = @"DELETE FROM [dbo].[LastFileUploadedToNextCloud]";
				dbCommand.ExecuteNonQuery();
			}
			catch (Exception ex)
			{
				ExceptionHandler.LogExceptionToConsole(ex);
				throw ex;
			}
			finally
			{
				if ((_dbConnection.State & ConnectionState.Open) != 0) _dbConnection.Close();
			}
		}
	}
}