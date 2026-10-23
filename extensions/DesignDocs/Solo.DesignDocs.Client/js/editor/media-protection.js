import { Annotation, EditorState } from '@codemirror/state';
export const mediaEdit = Annotation.define();
export function protectMedia(field, notify = () => {}) {
    return EditorState.transactionFilter.of(transaction => {
        if (!transaction.docChanged || transaction.annotation(mediaEdit) || !transaction.isUserEvent('delete')) return transaction;
        let blocked = false;
        transaction.changes.iterChangedRanges((from, to) => {
            transaction.startState.field(field).between(from, to, (a, b, decoration) => {
                if (decoration.spec.media && from < b && to > a) blocked = true;
            });
        });
        if (!blocked) return transaction;
        notify('Open the attachment viewer to remove photos or sketches.');
        return [];
    });
}
