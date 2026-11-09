enum WorkerAction: String {
    case library, search, preview
    case collections, collectionCreate, collectionRename, collectionArchive
    case collectionAdd, collectionRemove, sourceCollections
    case topics, topicReview, sourceMoves
    case intakeStatus, intakePause, intakeRescan
    case noteCapture, exportOriginal, verify, retry, revoke
    case mediaCues, mediaInspection, budgetSummary, budgetScenario
}
