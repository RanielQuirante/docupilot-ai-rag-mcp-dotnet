import { ChangeDetectionStrategy, Component } from '@angular/core';

import { PageShell } from '@shared/ui/page-shell/page-shell';

@Component({
  selector: 'app-semantic-search-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PageShell],
  templateUrl: './semantic-search-page.html',
})
export class SemanticSearchPage {}
