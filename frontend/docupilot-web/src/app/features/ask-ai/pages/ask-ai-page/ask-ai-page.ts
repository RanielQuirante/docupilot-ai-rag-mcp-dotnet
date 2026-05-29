import { ChangeDetectionStrategy, Component } from '@angular/core';

import { PageShell } from '@shared/ui/page-shell/page-shell';

@Component({
  selector: 'app-ask-ai-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PageShell],
  templateUrl: './ask-ai-page.html',
})
export class AskAiPage {}
