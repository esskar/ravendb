//-----------------------------------------------------------------------
// <copyright file="StorageActionsAccessor.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Microsoft.Isam.Esent.Interop;
using RavenFS.Util;

namespace RavenFS.Storage
{
	[CLSCompliant(false)]
	public class StorageActionsAccessor : IDisposable
	{
		private readonly TableColumnsCache tableColumnsCache;
		private readonly Session session;
		private readonly JET_DBID database;

		private Table files, usage, pages;
		private readonly Transaction transaction;

		private Table Files
		{
			get { return files ?? (files = new Table(session, database, "files", OpenTableGrbit.None)); }
		}

		private Table Usage
		{
			get { return usage ?? (usage = new Table(session, database, "usage", OpenTableGrbit.None)); }
		}

		private Table Pages
		{
			get { return pages ?? (pages = new Table(session, database, "pages", OpenTableGrbit.None)); }
		}

		public StorageActionsAccessor(TableColumnsCache tableColumnsCache, JET_INSTANCE instance, string databaseName)
		{
			this.tableColumnsCache = tableColumnsCache;
			try
			{
				session = new Session(instance);
				transaction = new Transaction(session);
				Api.JetOpenDatabase(session, databaseName, null, out database, OpenDatabaseGrbit.None);
			}
			catch (Exception)
			{
				Dispose();
				throw;
			}
		}
		
		public void Dispose()
		{
			if(pages != null)
				pages.Dispose();
			if (usage != null)
				usage.Dispose();
			if(files != null)
				files.Dispose();
			if(Equals(database, JET_DBID.Nil) == false)
				Api.JetCloseDatabase(session, database, CloseDatabaseGrbit.None);
			if(transaction != null)
				transaction.Dispose();
			if(session != null)
				session.Dispose();
		}

		public void Commit()
		{
			transaction.Commit(CommitTransactionGrbit.None);
		}

		public HashKey InsertPage(byte[] buffer, int size)
		{
			var key = new HashKey(buffer, size);

			Api.JetSetCurrentIndex(session, Pages, "by_keys");

			Api.MakeKey(session, Pages, key.Weak, MakeKeyGrbit.NewKey);
			Api.MakeKey(session, Pages, key.Strong, MakeKeyGrbit.None);

			if(Api.TrySeek(session, Pages, SeekGrbit.SeekEQ))
			{
				Api.EscrowUpdate(session, Pages, tableColumnsCache.PagesColumns["usage_count"], 1); 
				return key;
			}

			using (var update = new Update(session, Pages, JET_prep.Insert))
			{
				Api.SetColumn(session, Pages, tableColumnsCache.PagesColumns["page_strong_hash"], key.Strong);
				Api.SetColumn(session, Pages, tableColumnsCache.PagesColumns["page_weak_hash"], key.Weak);
				Api.JetSetColumn(session, Pages, tableColumnsCache.PagesColumns["data"], buffer, size,
				                 SetColumnGrbit.None, null);

				update.Save();
			}

			return key;
		}

		public void PutFile(string filename, long totalSize, NameValueCollection metadata)
		{
			using (var update = new Update(session, Files, JET_prep.Insert))
			{
				Api.SetColumn(session, Files, tableColumnsCache.FilesColumns["name"], filename, Encoding.Unicode);
				Api.SetColumn(session, Files, tableColumnsCache.FilesColumns["total_size"], BitConverter.GetBytes(totalSize));
				Api.SetColumn(session, Files, tableColumnsCache.FilesColumns["uploaded_size"], BitConverter.GetBytes(0));
				Api.SetColumn(session, Files, tableColumnsCache.FilesColumns["metadata"], ToQueryString(metadata), Encoding.Unicode);
				
				update.Save();
			}
		}

		private static string ToQueryString(NameValueCollection metadata)
		{
			var sb = new StringBuilder();
			foreach (var key in metadata.AllKeys)
			{
				var values = metadata.GetValues(key);
				if(values == null)
					continue;

				foreach (var value in values)
				{
					sb.Append(key)
						.Append("=")
						.Append(Uri.EscapeUriString(value))
						.Append("&");
				}
			}
			if (sb.Length > 0)
				sb.Length = sb.Length - 1;

			return sb.ToString();
		}

		public void AssociatePage(string filename, HashKey pageKey, int pagePositionInFile, int pageSize)
		{
			Api.JetSetCurrentIndex(session, Files, "by_name");
			Api.MakeKey(session, Files, filename, Encoding.Unicode, MakeKeyGrbit.NewKey);
			if(Api.TrySeek(session, Files, SeekGrbit.SeekEQ) == false)
				throw new FileNotFoundException("Could not find file: " + filename);

			using (var update = new Update(session, Files, JET_prep.Replace))
			{
				var totalSize = Api.RetrieveColumnAsInt32(session, Files, tableColumnsCache.FilesColumns["total_size"]).Value;
				var uploadedSize = Api.RetrieveColumnAsInt32(session, Files, tableColumnsCache.FilesColumns["uploaded_size"]).Value;

				if(uploadedSize+pageSize > totalSize)
					throw new InvalidDataException("Try to upload more data than the file was allocated for (" + totalSize +
					                               ") and new size would be: " + (uploadedSize + pageSize));

				Api.SetColumn(session, Files, tableColumnsCache.FilesColumns["uploaded_size"], uploadedSize + pageSize);

				update.Save();
			}

			using (var update = new Update(session, Usage, JET_prep.Insert))
			{
				Api.SetColumn(session, Usage, tableColumnsCache.UsageColumns["name"], filename, Encoding.Unicode);
				Api.SetColumn(session, Usage, tableColumnsCache.UsageColumns["file_pos"], pagePositionInFile);
				Api.SetColumn(session, Usage, tableColumnsCache.UsageColumns["page_strong_hash"], pageKey.Strong);
				Api.SetColumn(session, Usage, tableColumnsCache.UsageColumns["page_weak_hash"], pageKey.Weak);
				Api.SetColumn(session, Usage, tableColumnsCache.UsageColumns["page_size"], pageSize);

				update.Save();
			}
		}

		public int ReadPage(HashKey key, byte[] buffer)
		{
			Api.JetSetCurrentIndex(session, Pages, "by_keys");
			Api.MakeKey(session, Pages, key.Weak,MakeKeyGrbit.NewKey);
			Api.MakeKey(session, Pages,key.Strong, MakeKeyGrbit.None);

			if (Api.TrySeek(session, Pages, SeekGrbit.SeekEQ) == false)
				return -1;

			int size;
			Api.JetRetrieveColumn(session, Pages, tableColumnsCache.PagesColumns["data"], buffer, buffer.Length, out size,
			                      RetrieveColumnGrbit.None, null);
			return size;
		}

		public FileHeader ReadFile(string filename)
		{
			Api.JetSetCurrentIndex(session, Files, "by_name");
			Api.JetSetCurrentIndex(session, Files, "by_name");
			Api.MakeKey(session, Files, filename, Encoding.Unicode, MakeKeyGrbit.NewKey);
			if (Api.TrySeek(session, Files, SeekGrbit.SeekEQ) == false)
				return null;

			var metadata = Api.RetrieveColumnAsString(session, Files, tableColumnsCache.FilesColumns["metadata"], Encoding.Unicode);
			return new FileHeader
			{
				Name = Api.RetrieveColumnAsString(session, Files, tableColumnsCache.FilesColumns["name"], Encoding.Unicode),
				TotalSize = BitConverter.ToInt64(Api.RetrieveColumn(session, Files, tableColumnsCache.FilesColumns["total_size"]), 0),
				UploadedSize = BitConverter.ToInt64(Api.RetrieveColumn(session, Files, tableColumnsCache.FilesColumns["uploaded_size"]), 0),
				Metadata = HttpUtility.ParseQueryString(metadata)
			};
		}

		public FileAndPages GetFile(string filename, int start, int pagesToLoad)
		{
			Api.JetSetCurrentIndex(session, Files, "by_name");
			Api.MakeKey(session, Files, filename, Encoding.Unicode, MakeKeyGrbit.NewKey);
			if (Api.TrySeek(session, Files, SeekGrbit.SeekEQ) == false)
				throw new FileNotFoundException("Could not find file: " + filename);

			var metadata = Api.RetrieveColumnAsString(session, Files, tableColumnsCache.FilesColumns["metadata"],Encoding.Unicode);
			var fileInformation = new FileAndPages
			{
				TotalSize = BitConverter.ToInt64(Api.RetrieveColumn(session, Files, tableColumnsCache.FilesColumns["total_size"]), 0),
				UploadedSize = BitConverter.ToInt64(Api.RetrieveColumn(session, Files, tableColumnsCache.FilesColumns["uploaded_size"]), 0),
				Metadata = HttpUtility.ParseQueryString(metadata),
				Name = filename,
				Start = start
			};

			if(pagesToLoad > 0)
			{
				Api.JetSetCurrentIndex(session, Usage, "by_name_and_pos");
				Api.MakeKey(session, Usage, filename, Encoding.Unicode, MakeKeyGrbit.NewKey);
				Api.MakeKey(session, Usage, start, MakeKeyGrbit.None);
				if (Api.TrySeek(session, Usage, SeekGrbit.SeekGE))
				{
					Api.MakeKey(session, Usage, filename, Encoding.Unicode, MakeKeyGrbit.NewKey);
					Api.JetSetIndexRange(session, Usage, SetIndexRangeGrbit.RangeInclusive);

					do
					{
						fileInformation.Pages.Add(new PageInformation
						{
							Size = Api.RetrieveColumnAsInt32(session, Usage, tableColumnsCache.UsageColumns["page_size"]).Value,
							Key = new HashKey
							{
								Strong = Api.RetrieveColumn(session, Usage, tableColumnsCache.UsageColumns["page_strong_hash"]),
								Weak = Api.RetrieveColumnAsInt32(session, Usage, tableColumnsCache.UsageColumns["page_weak_hash"]).Value,
							}
						});
					} while (Api.TryMoveNext(session, Usage) && fileInformation.Pages.Count < pagesToLoad);
				}
			}

			return fileInformation;
		}

		public IEnumerable<FileHeader> ReadFiles(int start, int size)
		{
			Api.JetSetCurrentIndex(session, Files, "by_name");
			if(Api.TryMoveFirst(session, Files) == false)
				yield break;

			Api.JetMove(session, Files, start, MoveGrbit.None);

			int index = 0;

			do
			{
				var metadata = Api.RetrieveColumnAsString(session, Files, tableColumnsCache.FilesColumns["metadata"], Encoding.Unicode);
				yield return new FileHeader
				{
					Name = Api.RetrieveColumnAsString(session, Files, tableColumnsCache.FilesColumns["name"], Encoding.Unicode),
					TotalSize = BitConverter.ToInt64(Api.RetrieveColumn(session, Files, tableColumnsCache.FilesColumns["total_size"]), 0),
					UploadedSize = BitConverter.ToInt64(Api.RetrieveColumn(session, Files, tableColumnsCache.FilesColumns["uploaded_size"]), 0),
					Metadata = HttpUtility.ParseQueryString(metadata)
				};

			} while (++index < size && Api.TryMoveNext(session, Files));
		}

		public void Delete(string filename)
		{
			Api.JetSetCurrentIndex(session, Files, "by_name");
			Api.MakeKey(session, Files, filename, Encoding.Unicode, MakeKeyGrbit.NewKey);
			if (Api.TrySeek(session, Files, SeekGrbit.SeekEQ) == false)
				return;
			Api.JetDelete(session, Files);

			Api.JetSetCurrentIndex(session, Usage, "by_name_and_pos");
			Api.MakeKey(session, Usage, filename, Encoding.Unicode, MakeKeyGrbit.NewKey);
			if (!Api.TrySeek(session, Usage, SeekGrbit.SeekGE)) 
				return;

			Api.MakeKey(session, Usage, filename, Encoding.Unicode, MakeKeyGrbit.NewKey);
			Api.JetSetIndexRange(session, Usage, SetIndexRangeGrbit.RangeInclusive);

			Api.JetSetCurrentIndex(session, Pages, "by_keys");

			do
			{
				var page = new HashKey
				{
					Strong = Api.RetrieveColumn(session, Usage, tableColumnsCache.UsageColumns["page_strong_hash"]),
					Weak = Api.RetrieveColumnAsInt32(session, Usage, tableColumnsCache.UsageColumns["page_weak_hash"]).Value,
				};

				Api.MakeKey(session, Pages, page.Weak, MakeKeyGrbit.NewKey);
				Api.MakeKey(session, Pages, page.Strong, MakeKeyGrbit.None);

				if(Api.TrySeek(session, Pages, SeekGrbit.SeekEQ))
				{
					var escrowUpdate = Api.EscrowUpdate(session, Pages, tableColumnsCache.PagesColumns["usage_count"], -1);
					if(escrowUpdate <= 1)
					{
						Api.JetDelete(session, Pages);
					}
				}

				Api.JetDelete(session, Usage);
			} while (Api.TryMoveNext(session, Usage));
		}

	    public void UpdateFileMetadata(string filename, NameValueCollection metadata)
	    {
            Api.JetSetCurrentIndex(session, Files, "by_name");
            Api.MakeKey(session, Files, filename, Encoding.Unicode, MakeKeyGrbit.NewKey);
            if (Api.TrySeek(session, Files, SeekGrbit.SeekEQ) == false)
                throw new FileNotFoundException(filename);
		
            using (var update = new Update(session, Files, JET_prep.Replace))
            {
                Api.SetColumn(session, Files, tableColumnsCache.FilesColumns["metadata"], ToQueryString(metadata), Encoding.Unicode);

                update.Save();
            }
	    }
	}
}
