import { CurrencyPipe, DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { EMPTY, catchError, forkJoin, interval, switchMap } from 'rxjs';
import { GROUP_ORDER_PREVIEW } from '@app/shared/mocks/group-orders/group-order.preview';
import { ParticipantCardComponent } from '@app/components/group-orders/participant-card/participant-card/participant-card.component';
import { GroupSessionsResponse } from '@app/models/group-orders/group-sessions-response.model';
import { GroupReservationStatus } from '@app/models/group-orders/group-status.model';
import { Participant } from '@app/models/group-orders/participant.model';
import { GuestInvite } from '@app/models/reservations/guest-invite.model';
import { GroupOrdersService } from '@app/services/group-orders/group-orders.service';
import { ToastService } from '@app/services/toast/toast.service';
import { ModalComponent } from '@app/shared/custom-components/modal/modal.component';
import { consolidatePreOrder } from '@app/shared/helpers/consolidate-pre-order';

const SYNC_INTERVAL_MS = 15_000;

@Component({
  selector: 'app-group-order-page',
  imports: [CurrencyPipe, DatePipe, ModalComponent, ParticipantCardComponent],
  templateUrl: './group-order-page.component.html',
  changeDetection: ChangeDetectionStrategy.Eager,
})
export class GroupOrderPageComponent {
  private readonly route = inject(ActivatedRoute, { optional: true });
  private readonly service = this.route ? inject(GroupOrdersService) : null;
  private readonly toast = inject(ToastService, { optional: true });
  private readonly destroyRef = inject(DestroyRef);
  private readonly reservationId = this.route?.snapshot.paramMap.get('reservationId') ?? null;

  readonly invite = signal<GuestInvite | null>(GROUP_ORDER_PREVIEW.invite);
  readonly participants = signal<Participant[]>(GROUP_ORDER_PREVIEW.sessions.participants);
  readonly ownerSessionId = signal<string | null>(GROUP_ORDER_PREVIEW.ownerSessionId);
  readonly groupStatus = signal<GroupReservationStatus>('Open');
  readonly loading = signal(false);
  readonly loadError = signal<string | null>(null);
  readonly syncError = signal(false);
  readonly lastUpdatedAt = signal<Date | null>(null);
  readonly submitting = signal(false);
  readonly submitDialogOpen = signal(false);
  readonly removeTarget = signal<Participant | null>(null);
  readonly removingGuestId = signal<string | null>(null);

  readonly consolidatedOrder = computed(() => consolidatePreOrder(this.participants()));
  readonly grandTotal = computed(() =>
    this.service ? this.consolidatedOrder().total : GROUP_ORDER_PREVIEW.sessions.total,
  );
  readonly unsubmittedCount = computed(() => this.participants().filter((participant) => !participant.isSubmitted).length);
  readonly hasAnyDishes = computed(() => this.participants().some((participant) => participant.items.length > 0));
  readonly isSubmitted = computed(() => this.groupStatus() === 'Submitted');
  readonly canSubmit = computed(
    () => !this.isSubmitted() && !this.submitting() && this.consolidatedOrder().items.length > 0,
  );
  readonly currentParticipantCount = computed(() => this.participants().length);

  constructor() {
    if (this.service && this.reservationId) {
      this.load();
      interval(SYNC_INTERVAL_MS)
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe(() => {
          if (this.groupStatus() === 'Open' && !this.isSubmitted()) {
            this.refresh();
          }
        });
    } else {
      this.lastUpdatedAt.set(new Date());
    }
  }

  load(): void {
    const service = this.service;
    const reservationId = this.reservationId;
    if (!service || !reservationId) {
      return;
    }

    this.loading.set(true);
    this.loadError.set(null);
    service
      .getGroupInvite$(reservationId)
      .pipe(
        switchMap((invite) => {
          this.invite.set(invite);
          return forkJoin({
            sessions: service.getSessions$(invite.GroupReservationId),
            ownerSessionId: service.getOwnerSessionId$(reservationId),
            status: service.getStatus$(invite.GroupReservationId),
          });
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: ({ sessions, ownerSessionId, status }) => {
          this.applySessions(sessions);
          this.ownerSessionId.set(ownerSessionId);
          this.groupStatus.set(status.status);
          this.lastUpdatedAt.set(new Date());
          this.loading.set(false);
        },
        error: (error: unknown) => {
          this.loadError.set(
            error instanceof HttpErrorResponse && error.status === 404
              ? 'Group reservation not found'
              : 'Unable to load the group reservation.',
          );
          this.loading.set(false);
        },
      });
  }

  refresh(): void {
    const groupReservationId = this.invite()?.GroupReservationId;
    if (!this.service || !groupReservationId || this.refreshInProgress) {
      return;
    }

    this.refreshInProgress = true;
    forkJoin({
      sessions: this.service.getSessions$(groupReservationId, { silent: true }),
      status: this.service.getStatus$(groupReservationId, { silent: true }),
    })
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        catchError(() => {
          this.syncError.set(true);
          return EMPTY;
        }),
      )
      .subscribe({
        next: ({ sessions, status }) => {
          this.applySessions(sessions);
          this.groupStatus.set(status.status);
          this.lastUpdatedAt.set(new Date());
          this.syncError.set(false);
        },
        complete: () => {
          this.refreshInProgress = false;
        },
      });
  }

  retry(): void {
    this.load();
  }

  openSubmitDialog(): void {
    if (this.canSubmit()) {
      this.submitDialogOpen.set(true);
    }
  }

  closeSubmitDialog(): void {
    if (!this.submitting()) {
      this.submitDialogOpen.set(false);
    }
  }

  submitOrder(): void {
    const groupReservationId = this.invite()?.GroupReservationId;
    if (!this.service || !this.reservationId || !groupReservationId || !this.canSubmit()) {
      return;
    }

    this.submitting.set(true);
    this.service
      .submit$(groupReservationId, this.reservationId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.groupStatus.set('Submitted');
          this.submitDialogOpen.set(false);
          this.toast?.success('Pre-order sent to the restaurant');
        },
        error: () => this.submitting.set(false),
        complete: () => this.submitting.set(false),
      });
  }

  requestRemove(participant: Participant): void {
    if (participant.type === 'Guest' && !this.isSubmitted()) {
      this.removeTarget.set(participant);
    }
  }

  closeRemoveDialog(): void {
    if (!this.removingGuestId()) {
      this.removeTarget.set(null);
    }
  }

  removeGuest(): void {
    const participant = this.removeTarget();
    const groupReservationId = this.invite()?.GroupReservationId;
    if (
      !this.service ||
      !participant ||
      !groupReservationId ||
      participant.type !== 'Guest' ||
      this.isSubmitted() ||
      this.removingGuestId()
    ) {
      return;
    }

    this.removingGuestId.set(participant.id);
    this.service
      .removeSession$(groupReservationId, participant.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (sessions) => {
          this.applySessions(sessions);
          this.removeTarget.set(null);
        },
        error: () => this.removingGuestId.set(null),
        complete: () => this.removingGuestId.set(null),
      });
  }

  canRemoveParticipant(participant: Participant): boolean {
    return participant.type === 'Guest' && !this.isSubmitted();
  }

  private refreshInProgress = false;

  private applySessions(sessions: GroupSessionsResponse): void {
    this.participants.set(sessions.participants);
  }
}