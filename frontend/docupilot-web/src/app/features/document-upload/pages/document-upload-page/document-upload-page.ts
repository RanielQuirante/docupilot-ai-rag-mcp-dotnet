import { ChangeDetectionStrategy, Component } from '@angular/core';

import { PageShell } from '@shared/ui/page-shell/page-shell';

@Component({
  selector: 'app-document-upload-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PageShell],
  templateUrl: './document-upload-page.html',
})
export class DocumentUploadPage {}
