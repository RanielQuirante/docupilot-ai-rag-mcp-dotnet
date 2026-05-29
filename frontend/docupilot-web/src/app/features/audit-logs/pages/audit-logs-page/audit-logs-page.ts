import { ChangeDetectionStrategy, Component } from '@angular/core';

import { PageShell } from '@shared/ui/page-shell/page-shell';

@Component({
  selector: 'app-audit-logs-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PageShell],
  templateUrl: './audit-logs-page.html',
})
export class AuditLogsPage {}
