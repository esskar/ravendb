﻿namespace RavenFS.Rdc.Conflictuality
{
	public class ConflictItem
    {
        public HistoryItem Remote { get; set; }
        public HistoryItem Current { get; set; }
    }
}