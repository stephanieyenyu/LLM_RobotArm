# User-authorized manual retry

The user explicitly requested: 先讓我跑跑看.

The temporary demo_motion_blocked.txt lock and its Unity meta were backed up and moved under csharp_server/outputs/manual_retry_backup_dfa3a31e165d4487bd3284585361d18e. Before removal, current_step.json was confirmed to contain done=true and user_input.txt was empty. No robot program was submitted or robot recovery action initiated.

The per-sample 0.16 m TCP base exclusion, 90-degree joint-transition guard, controller feedback gates, and safety-stop latch remain active. A new protective stop recreates the persistent lock and does not issue an automatic Home command.

The reported collision path and pendant pose remain regression cases. Physical tool geometry and a replacement trajectory have not been validated on hardware; permission to retry does not establish collision clearance.
