import { ChangeDetectionStrategy, Component } from '@angular/core';

import { PageShell } from '@shared/ui/page-shell/page-shell';

@Component({
  selector: 'app-document-library-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PageShell],
  templateUrl: './document-library-page.html',
})
export class DocumentLibraryPage {}
