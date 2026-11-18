struct CommandHistory: Decodable {
    let messageSequence: Int
    let receipt: CommandReceipt
}
