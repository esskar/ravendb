namespace RavenFS.Rdc.Conflictuality
{
	using System;
	using System.Collections.Specialized;
	using Client;
	using Notifications;

	public class ConflictResolver
	{
		public bool IsResolved(NameValueCollection destinationMetadata, ConflictItem conflict)
		{
			var conflictResolutionString = destinationMetadata[SynchronizationConstants.RavenReplicationConflictResolution];
			if (String.IsNullOrEmpty(conflictResolutionString))
			{
				return false;
			}
			var conflictResolution = new TypeHidingJsonSerializer().Parse<ConflictResolution>(conflictResolutionString);
			return conflictResolution.Strategy == ConflictResolutionStrategy.RemoteVersion
				&& conflictResolution.RemoteServerId == conflict.Remote.ServerId;
		}
	}
}