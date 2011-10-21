using System;
using System.IO;
using System.Threading.Tasks;
using System.Web;
using Raven.Abstractions.Extensions;
using RavenFS.Infrastructure;
using RavenFS.Storage;

namespace RavenFS.Handlers
{
	[HandlerMetadata("^/files/(.+)", "HEAD")]
	public class SimpleHeadHandler : AbstractAsyncHandler
	{
		protected override Task ProcessRequestAsync(HttpContext context)
		{
			var filename = Url.Match(context.Request.CurrentExecutionFilePath).Groups[1].Value;
			FileAndPages fileAndPages = null;
			try
			{
				Storage.Batch(accessor => fileAndPages = accessor.GetFile(filename, 0, 0));
			}
			catch (FileNotFoundException)
			{
				context.Response.StatusCode = 404;

				return Completed;
			}

			MetadataExtensions.AddHeaders(context, fileAndPages);
			
			return Completed;
		}
	}
}